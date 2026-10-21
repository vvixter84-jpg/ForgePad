using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ForgePad.Core;

namespace ForgePad.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
    }

    /// <summary>У WinExe нет консоли: любая ошибка должна попадать в файл, иначе тихий выход.</summary>
    private static void Log(Exception? exception)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "forgepad-crash.log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // Логирование не должно падать само.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Служебный режим: ждём родителя и возвращаем микрофон, если тот упал.
        if (e.Args.Length >= 3 && e.Args[0] == "--watchdog")
        {
            int pid = int.Parse(e.Args[1]);
            string device = e.Args[2];

            try
            {
                using Process target = Process.GetProcessById(pid);
                target.WaitForExit();
            }
            catch
            {
                // Процесс уже завершился — всё равно возвращаем устройство.
            }

            try { AudioDevicePolicy.SetDefaultCapture(device); } catch { }

            Shutdown();
            return;
        }

        try
        {
            // Тему применяем до создания окна, чтобы не было вспышки чужой палитры.
            AppSettings settings = AppSettings.Load();
            ThemeManager.Apply(ThemeManager.ByKey(settings.Theme));

            int themeIndex = Array.IndexOf(e.Args, "--theme");

            if (themeIndex >= 0 && themeIndex + 1 < e.Args.Length)
                ThemeManager.Apply(ThemeManager.ByKey(e.Args[themeIndex + 1]));

            var window = new MainWindow(selfCheck: e.Args.Contains("--no-takeover"));
            MainWindow = window;
            window.Show();

            if (e.Args.Contains("--selftest-play"))
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2),
                };

                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    window.PlayFirstForTest();
                };

                timer.Start();
            }
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show("Не удалось открыть окно:\n\n" + ex.Message, "ForgePad");
            Shutdown(1);
        }
    }
}
