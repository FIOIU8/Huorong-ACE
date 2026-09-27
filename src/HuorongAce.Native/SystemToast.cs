using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using HuorongAce.Native.Win32;

namespace HuorongAce.Native;

/// <summary>
/// Raises real Windows notifications (Action Center toasts).
/// </summary>
/// <remarks>
/// <para>
/// A toast is raised through WinRT. For an unpackaged desktop app — no MSIX
/// identity — the reliable route is still to hand a script to PowerShell, which
/// is exactly what the Go build did. It has one very useful side effect:
/// PowerShell is a separate process, so the toast outlives our own exit, which
/// is what makes the shutdown notification visible at all.
/// </para>
/// <para>
/// A packaged WinUI app could use <c>AppNotificationManager</c> instead. This
/// port stays unpackaged to match the Go build's deployment model (a single
/// self-contained folder, no installer), so the PowerShell route is kept.
/// </para>
/// </remarks>
public static class SystemToast
{
    /// <summary>AppUserModelID used to attribute the notifications.</summary>
    public const string AppId = "com.huorong.ace";

    private const string ScriptWithImage = """
        $title = {0}
        $content = {1}
        $img = {2}
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
        $tpl = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastImageAndText02)
        $xd = [xml] $tpl.GetXml()
        $xd.GetElementsByTagName("text")[0].AppendChild($xd.CreateTextNode($title)) > $null
        $xd.GetElementsByTagName("text")[1].AppendChild($xd.CreateTextNode($content)) > $null
        $xd.GetElementsByTagName("image")[0].SetAttribute("src", $img) > $null
        $x = New-Object Windows.Data.Xml.Dom.XmlDocument
        $x.LoadXml($xd.OuterXml)
        $toast = [Windows.UI.Notifications.ToastNotification]::new($x)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier({3}).Show($toast);
        """;

    private const string ScriptTextOnly = """
        $title = {0}
        $content = {1}
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
        $tpl = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $xd = [xml] $tpl.GetXml()
        $xd.GetElementsByTagName("text")[0].AppendChild($xd.CreateTextNode($title)) > $null
        $xd.GetElementsByTagName("text")[1].AppendChild($xd.CreateTextNode($content)) > $null
        $x = New-Object Windows.Data.Xml.Dom.XmlDocument
        $x.LoadXml($xd.OuterXml)
        $toast = [Windows.UI.Notifications.ToastNotification]::new($x)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier({2}).Show($toast);
        """;

    private static readonly Lazy<string?> IconPath = new(CreateIconFile);

    /// <summary>
    /// Shows a toast and waits for it to be raised.
    /// </summary>
    /// <returns>False when PowerShell could not be used.</returns>
    public static bool TryShow(string title, string content)
    {
        var icon = IconPath.Value;
        var script = icon is null
            ? string.Format(CultureInfo.InvariantCulture, ScriptTextOnly,
                Quote(title), Quote(content), Quote(AppId))
            : string.Format(CultureInfo.InvariantCulture, ScriptWithImage,
                Quote(title), Quote(content), Quote("file:///" + icon.Replace('\\', '/')), Quote(AppId));

        return RunPowerShell(script);
    }

    /// <summary>Escapes a value for a PowerShell double-quoted string.</summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$") + "\"";

    private static bool RunPowerShell(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"huorong-ace-toast-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(path, script, new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "PowerShell",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(path);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine($"[toast] PowerShell 不可用: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }

    private static string? CreateIconFile()
    {
        try
        {
            var target = Path.Combine(Path.GetTempPath(), "huorong-ace-toast-icon.png");
            using var bitmap = new Bitmap(64, 64);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var background = new SolidBrush(Color.FromArgb(0xC0, 0x00, 0x00));
                graphics.FillRectangle(background, 0, 0, 64, 64);
                using var shield = ShieldIcon.ShieldPath(64);
                using var foreground = new SolidBrush(Color.White);
                graphics.FillPath(foreground, shield);
            }

            bitmap.Save(target, ImageFormat.Png);
            return target;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
