using System;
using System.Diagnostics;
using System.Windows;
using System.Threading.Tasks;
using ForgePad.Core;

namespace ForgePad.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "ui");

            // Ошибка в интерфейсе не должна убивать приложение и показывать
            // системное «неизвестное исключение» без деталей.
            e.Handled = true;

            MessageBox.Show(
                "Что-то пошло не так:\n\n" + e.Exception.Message +
                "\n\nПодробности записаны в " + Log.FilePath,
                "ForgePad",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error(e.ExceptionObject as Exception ?? new Exception("unknown"), "appdomain");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "task");
            e.SetObserved();
        };
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

            Log.Write("started");

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
            Log.Error(ex, "startup");
            MessageBox.Show("Не удалось открыть окно:\n\n" + ex.Message + "\n\nЛог: " + Log.FilePath, "ForgePad");
            Shutdown(1);
        }
    }
}
