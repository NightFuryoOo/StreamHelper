using System;
using System.Diagnostics;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

internal static class BrowserLauncher
{
    public static void Open(string url)
    {
        if (Environment.GetEnvironmentVariable("STREAMHELPER_NO_BROWSER") == "1")
        {
            Log.Write("Browser not opened (test mode): " + url);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Open browser failed: " + ex.Message);
        }
    }

    public static void ShowFile(string path)
    {
        if (Environment.GetEnvironmentVariable("STREAMHELPER_NO_BROWSER") == "1")
        {
            Log.Write("Explorer not opened (test mode): " + path);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Write("Open explorer failed: " + ex.Message);
        }
    }
}
