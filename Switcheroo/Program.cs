/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * http://www.switcheroo.io/
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 * 
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using Switcheroo.Properties;

namespace Switcheroo
{
    internal class Program
    {
        private const string mutex_id = "DBDE24E4-91F6-11DF-B495-C536DFD72085-switcheroo";

        [STAThread]
        private static void Main()
        {
            ConfigureLogging();

            Trace.WriteLine(string.Format("=== Switcheroo {0} starting ===",
                Assembly.GetExecutingAssembly().GetName().Version));
            Trace.WriteLine("CurrentDirectory=" + Environment.CurrentDirectory);
            Trace.WriteLine("CommandLine=" + Environment.CommandLine);
            Trace.WriteLine("IsAdmin=" + IsRunAsAdmin() + ", RunAsAdminSetting=" + Settings.Default.RunAsAdmin);
            Trace.WriteLine("EnableHotKey=" + Settings.Default.EnableHotKey +
                            ", CurEnableHotKey=" + Settings.Default.CurEnableHotKey +
                            ", AltTabHook=" + Settings.Default.AltTabHook +
                            ", FirstRun=" + Settings.Default.FirstRun);
            Trace.WriteLine("Main hotkey: KeyCode=" + Settings.Default.HotKey +
                            " Alt=" + Settings.Default.Alt +
                            " Ctrl=" + Settings.Default.Ctrl +
                            " Shift=" + Settings.Default.Shift +
                            " Win=" + Settings.Default.WindowsKey +
                            " Name=" + Settings.Default.HotKeyName);
            Trace.WriteLine("Cur hotkey: KeyCode=" + Settings.Default.CurHotKey +
                            " Alt=" + Settings.Default.CurAlt +
                            " Ctrl=" + Settings.Default.CurCtrl +
                            " Shift=" + Settings.Default.CurShift +
                            " Win=" + Settings.Default.CurWindowsKey +
                            " Name=" + Settings.Default.CurHotKeyName);

            RunAsAdministratorIfConfigured();

            using (var mutex = new Mutex(false, mutex_id))
            {
                var hasHandle = false;
                try
                {
                    try
                    {
                        hasHandle = mutex.WaitOne(5000, false);
                        if (hasHandle == false)
                        {
                            Trace.WriteLine("Another instance is already running - exiting.");
                            return; //another instance exist
                        }
                        Trace.WriteLine("Single-instance mutex acquired.");
                    }
                    catch (AbandonedMutexException)
                    {
                        Trace.WriteLine("Mutex was abandoned - acquiring it.");
                        hasHandle = true;
                    }

#if PORTABLE
                        MakePortable(Settings.Default);
#endif

                    MigrateUserSettings();

                    try
                    {
                        Trace.WriteLine("Creating App and MainWindow...");
                        var app = new App
                        {
                            MainWindow = new MainWindow()
                        };
                        Trace.WriteLine("MainWindow created. Starting WPF message loop (app.Run)...");
                        app.Run();
                        Trace.WriteLine("app.Run returned - application exited normally.");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("FATAL UNHANDLED EXCEPTION: " + ex);
                        throw;
                    }
                }
                finally
                {
                    if (hasHandle)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static void ConfigureLogging()
        {
            try
            {
                var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switcheroo");
                Directory.CreateDirectory(logDirectory);
                var logPath = Path.Combine(logDirectory, "switcheroo.log");
                Trace.Listeners.Clear();
                var fileStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                Trace.Listeners.Add(new TextWriterTraceListener(fileStream)
                {
                    TraceOutputOptions = TraceOptions.DateTime | TraceOptions.ThreadId
                });
                Trace.AutoFlush = true;
                Trace.WriteLine(string.Format("--- process started at {0} (PID {1}) ---",
                    DateTime.Now, Process.GetCurrentProcess().Id));
            }
            catch (Exception ex)
            {
                // Logging must never prevent the app from starting
                System.Diagnostics.Debug.WriteLine("Failed to configure logging: " + ex);
            }
        }

        private static void RunAsAdministratorIfConfigured()
        {
            if (RunAsAdminRequested() && !IsRunAsAdmin())
            {
                Trace.WriteLine("Restarting elevated as administrator (UAC)...");
                ProcessStartInfo proc = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory,
                    FileName = Assembly.GetEntryAssembly().CodeBase,
                    Verb = "runas"
                };

                Process.Start(proc);
                Trace.WriteLine("Elevated process launched - exiting current instance.");
                Environment.Exit(0);
            }
        }

        private static bool RunAsAdminRequested()
        {
            return Settings.Default.RunAsAdmin;
        }

        private static void MakePortable(ApplicationSettingsBase settings)
        {
            var portableSettingsProvider = new PortableSettingsProvider();
            settings.Providers.Add(portableSettingsProvider);
            foreach (SettingsProperty prop in settings.Properties)
            {
                prop.Provider = portableSettingsProvider;
            }
            settings.Reload();
        }

        private static void MigrateUserSettings()
        {
            if (!Settings.Default.FirstRun) return;

            Settings.Default.Upgrade();
            Settings.Default.FirstRun = false;
            Settings.Default.Save();
        }

        private static bool IsRunAsAdmin()
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(id);

            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}