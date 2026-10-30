using System.Security.Principal;

namespace ExternalIp.Widget;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExternalIpWidget");
        var profileIndex = Array.IndexOf(args, "--profile");
        if (profileIndex >= 0 && profileIndex + 1 < args.Length) profile = Path.GetFullPath(args[profileIndex + 1]);
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var instanceName = $"Local\\ExternalIpWidget.{user}";
        using var mutex = new Mutex(false, instanceName);
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            try { using var existing = EventWaitHandle.OpenExisting(instanceName + (args.Contains("--settings") ? ".Settings" : ".Show")); existing.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
            using var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Settings");
            using var context = new TrayApplication(profile, signal, settingsSignal, args.Contains("--settings"));
            Application.Run(context);
        }
        catch (Exception e)
        { MessageBox.Show("Не удалось запустить External IP Widget.\n\n" + e.Message, "External IP Widget", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { mutex.ReleaseMutex(); }
    }
}
