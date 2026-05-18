using System;
using System.Windows;
using System.Windows.Threading;

namespace VENM
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 🔹 Глобальная обработка исключений в UI-потоке
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // 🔹 Глобальная обработка исключений в фоновых потоках
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"Произошла ошибка в интерфейсе:\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "Критическая ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // e.Handled = true предотвратит аварийное закрытие приложения
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            MessageBox.Show(
                $"Произошла критическая ошибка в фоне:\n{ex?.Message}",
                "Критическая ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}