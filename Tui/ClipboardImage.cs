using System.ComponentModel;
using System.Diagnostics;

namespace Qwek.Tui;

public static class ClipboardImage
{
    public static async Task<(bool Success, string Path, string Error)> TryGetImageAsync(
        string directory,
        CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string basePath = Path.Combine(directory, "pasted-image");

            if (OperatingSystem.IsWindows())
            {
                return await TryGetWindowsAsync(basePath, ct);
            }

            if (OperatingSystem.IsLinux())
            {
                return await TryGetLinuxAsync(basePath, ct);
            }

            return (false, "", "Pasting images is not supported on this platform.");
        }
        catch (OperationCanceledException)
        {
            return (false, "", "Reading the clipboard timed out.");
        }
        catch (Exception ex)
        {
            return (false, "", "Could not read the clipboard: " + ex.Message);
        }
    }

    private static async Task<(bool Success, string Path, string Error)>
        TryGetWindowsAsync(string basePath, CancellationToken ct)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            try {
                Add-Type -AssemblyName System.Windows.Forms
                Add-Type -AssemblyName System.Drawing

                $base = $env:QWEK_CLIPBOARD_IMAGE_BASE
                $image = $null

                if ([System.Windows.Forms.Clipboard]::ContainsImage()) {
                    $image = [System.Windows.Forms.Clipboard]::GetImage()
                }

                if ($null -eq $image) {
                    $data = [System.Windows.Forms.Clipboard]::GetDataObject()
                    if ($null -ne $data -and $data.GetDataPresent('PNG')) {
                        $stream = $data.GetData('PNG')
                        if ($stream -is [System.IO.Stream]) {
                            if ($stream.CanSeek) { $stream.Position = 0 }
                            $image = [System.Drawing.Image]::FromStream($stream)
                        }
                    }
                }

                if ($null -ne $image) {
                    $dest = $base + '.png'
                    $image.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
                    $image.Dispose()
                    Write-Output $dest
                    exit 0
                }

                $files = $null
                if ([System.Windows.Forms.Clipboard]::ContainsFileDropList()) {
                    $files = [System.Windows.Forms.Clipboard]::GetFileDropList()
                }

                if ($null -ne $files) {
                    foreach ($file in $files) {
                        if ($file -notmatch '\.(png|jpe?g|gif|bmp|webp)$') { continue }

                        $img = $null
                        try {
                            $img = [System.Drawing.Image]::FromFile($file)
                            $dest = $base + '.png'
                            $img.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
                            $img.Dispose()
                        } catch {
                            if ($null -ne $img) { $img.Dispose() }
                            $ext = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
                            $dest = $base + $ext
                            Copy-Item -LiteralPath $file -Destination $dest -Force
                        }

                        Write-Output $dest
                        exit 0
                    }
                }

                exit 2
            } catch {
                exit 3
            }
            """;

        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-STA");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        startInfo.Environment["QWEK_CLIPBOARD_IMAGE_BASE"] = basePath;

        using Process process = new() { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return (false, "", "Could not start PowerShell to read the clipboard.");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return (false, "", "Reading the clipboard timed out.");
        }

        string output = (await stdout).Trim();
        _ = await stderr;

        if (process.ExitCode == 2)
        {
            return (false, "", "No image found in the clipboard.");
        }

        if (process.ExitCode != 0 || output.Length == 0)
        {
            return (false, "", "Could not read an image from the clipboard.");
        }

        if (!File.Exists(output) || new FileInfo(output).Length == 0)
        {
            return (false, "", "Could not read an image from the clipboard.");
        }

        return (true, output, "");
    }

    private static async Task<(bool Success, string Path, string Error)>
        TryGetLinuxAsync(string basePath, CancellationToken ct)
    {
        string destination = basePath + ".png";

        (string Command, string[] Arguments)[] tools =
        [
            ("wl-paste", ["--no-newline", "--type", "image/png"]),
            ("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"])
        ];

        foreach ((string command, string[] arguments) in tools)
        {
            if (await TryRunLinuxToolAsync(command, arguments, destination, ct))
            {
                return (true, destination, "");
            }
        }

        return (false, "",
            "No image found in the clipboard. Install wl-paste or xclip.");
    }

    private static async Task<bool> TryRunLinuxToolAsync(
        string command,
        string[] arguments,
        string destination,
        CancellationToken ct)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return false;
        }

        using MemoryStream buffer = new();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(
                buffer,
                timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return false;
        }

        _ = await stderr;

        if (process.ExitCode != 0 || buffer.Length == 0)
        {
            return false;
        }

        await File.WriteAllBytesAsync(destination, buffer.ToArray(), ct);
        return true;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
