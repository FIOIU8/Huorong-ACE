using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using HuorongAce.Native.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace HuorongAce.Native;

/// <summary>
/// Raises real Windows notifications (Action Center toasts).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the Go build's approach was not enough.</b> It handed a script to
/// PowerShell, on the theory that PowerShell being a separate process lets the
/// toast outlive our own exit. That part was true, but the notifications still
/// never appeared, because the real precondition was missing: an unpackaged
/// desktop app has no shell identity, and Windows silently discards toasts from
/// an app it cannot resolve to a Start-menu shortcut with an AppUserModelID.
/// <c>CreateToastNotifier</c> succeeds and <c>Show</c> does not throw — it just
/// never renders. See <see cref="AppIdentity"/>, which now installs that
/// identity at start-up.
/// </para>
/// <para>
/// With the identity in place the toast can be raised in-process through the
/// WinRT projection, which is faster than starting PowerShell and — unlike the
/// script — surfaces failures. Toast submission is handed to the system, so the
/// notification still outlives the process, which keeps the shutdown
/// notification working.
/// </para>
/// <para>
/// The PowerShell route is kept only as a fallback, for the case where the
/// in-process WinRT call is unavailable.
/// </para>
/// </remarks>
public static class SystemToast
{
    /// <summary>AppUserModelID, must match the one registered by <see cref="AppIdentity"/>.</summary>
    public static string AppId => AppIdentity.AppUserModelId;

    private static readonly Lazy<string?> IconPath = new(CreateIconFile);
    private static int _identityRegistered;

    /// <summary>
    /// Shows a toast and waits for it to be accepted.
    /// </summary>
    /// <returns>False when no notification route was available.</returns>
    /// <remarks>
    /// Failures are appended to <c>%TEMP%\huorong-ace-toast.log</c>. A toast
    /// that is accepted but never rendered leaves no other trace at all — the
    /// shell simply drops it — so without this log there is nothing to
    /// diagnose from.
    /// </remarks>
    /// <summary>
    /// Installs the shell identity early and reports whether the Start-menu
    /// shortcut had to be created just now.
    /// </summary>
    /// <remarks>
    /// Call this from start-up so the shell has the whole initialisation
    /// sequence to index the AUMID before the first toast is sent.
    /// </remarks>
    public static bool WarmUp()
    {
        EnsureIdentity();
        return AppIdentity.ShortcutCreatedThisRun;
    }

    public static bool TryShow(string title, string content)
    {
        EnsureIdentity();

        if (TryShowWinRt(title, content))
        {
            Trace($"WinRT 已提交: {title}");
            return true;
        }

        if (TryShowPowerShell(title, content))
        {
            Trace($"PowerShell 回退已提交: {title}");
            return true;
        }

        Trace($"通知发送失败: {title}");
        return false;
    }

    /// <summary>Appends a diagnostic line to the toast log, best effort.</summary>
    private static void Trace(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "huorong-ace-toast.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never break the feature they are diagnosing.
        }
    }

    /// <summary>
    /// Installs the shell identity once. Called lazily so a failure cannot stop
    /// the app from starting.
    /// </summary>
    private static void EnsureIdentity()
    {
        if (Interlocked.Exchange(ref _identityRegistered, 1) != 0)
        {
            return;
        }

        if (AppIdentity.Register(IconPath.Value))
        {
            Trace("应用身份已注册");
        }
        else
        {
            Trace("应用身份注册失败，通知将不会显示");
        }
    }

    private static bool TryShowWinRt(string title, string content)
    {
        try
        {
            var xml = ToastNotificationManager.GetTemplateContent(
                IconPath.Value is null ? ToastTemplateType.ToastText02 : ToastTemplateType.ToastImageAndText02);

            var textNodes = xml.GetElementsByTagName("text");
            if (textNodes.Count >= 2)
            {
                AppendText(xml, (XmlElement)textNodes[0], title);
                AppendText(xml, (XmlElement)textNodes[1], content);
            }

            if (IconPath.Value is not null)
            {
                var imageNodes = xml.GetElementsByTagName("image");
                if (imageNodes.Count > 0 && imageNodes[0] is XmlElement image)
                {
                    image.SetAttribute("src", "file:///" + IconPath.Value.Replace('\\', '/'));
                }
            }

            var toast = new ToastNotification(xml);
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            Trace($"WinRT 通知异常: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void AppendText(XmlDocument document, XmlElement element, string text)
    {
        element.AppendChild(document.CreateTextNode(text));
    }

    // ---------------------------------------------------------------------
    // PowerShell fallback
    // ---------------------------------------------------------------------

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

    private static bool TryShowPowerShell(string title, string content)
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
            using var bitmap = ShieldIcon.Render(64);
            bitmap.Save(target, ImageFormat.Png);
            return target;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }
}
