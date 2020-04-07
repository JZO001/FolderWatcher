using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace FolderWatcher
{

    class Program
    {

        private static string mDefaultFolderToWatch = (ConfigurationManager.AppSettings["DefaultFolderToWatch"] ?? AppDomain.CurrentDomain.SetupInformation.ApplicationBase);
        private static readonly List<string> mExcludeSubFolders = new List<string>();
        private static string mCommandAtChange = (ConfigurationManager.AppSettings["CommandAtChange"] ?? string.Empty);
        private static string mCommandWorkFolder = (ConfigurationManager.AppSettings["CommandWorkFolder"] ?? mDefaultFolderToWatch);
        private static int mCommandDelayMS = 1000;
        private static bool mCommandUseShellExecute = false;
        private static string mRefreshNotificationURL = (ConfigurationManager.AppSettings["RefreshNotificationURL"] ?? string.Empty);

        private static readonly List<FileSystemWatcher> mFileWatchers = new List<FileSystemWatcher>();

        private static Thread mExecutorThread = null;
        private static Process mExecutedProcess = null;

        static void Main(string[] args)
        {
            mExcludeSubFolders.AddRange((ConfigurationManager.AppSettings["ExcludeSubFolders"] ?? string.Empty).Split(";", StringSplitOptions.RemoveEmptyEntries));
            string commandDelayMSstr = ConfigurationManager.AppSettings["CommandDelayMS"] ?? "1000";
            if (int.TryParse(commandDelayMSstr, out mCommandDelayMS))
            {
                if (mCommandDelayMS < 0) mCommandDelayMS = 0;
            }

            if (args != null && args.Length > 0)
            {
                string pDefaultFolderToWatch = "/DefaultFolderToWatch=";
                string pExcludeSubFolders = "/ExcludeSubFolders=";
                string pCommandAtChange = "/CommandAtChange=";
                string pCommandWorkFolder = "/CommandWorkFolder=";
                string pCommandDelayMS = "/CommandDelayMS=";
                string pCommandUseShellExecute = "/UseShellExecute=";
                string pRefreshNotificationURL = "/RefreshNotificationURL=";
                foreach (string item in args)
                {
                    if (item.StartsWith(pDefaultFolderToWatch))
                    {
                        mDefaultFolderToWatch = item.Substring(pDefaultFolderToWatch.Length);
                    }
                    else if (item.StartsWith(pExcludeSubFolders))
                    {
                        mExcludeSubFolders.AddRange(item.Substring(pExcludeSubFolders.Length).Split(";", StringSplitOptions.RemoveEmptyEntries));
                    }
                    else if (item.StartsWith(pCommandAtChange))
                    {
                        mCommandAtChange = item.Substring(pCommandAtChange.Length);
                    }
                    else if (item.StartsWith(pCommandWorkFolder))
                    {
                        mCommandWorkFolder = item.Substring(pCommandWorkFolder.Length);
                    }
                    else if (item.StartsWith(pCommandDelayMS))
                    {
                        commandDelayMSstr = item.Substring(pCommandDelayMS.Length);
                        if (int.TryParse(commandDelayMSstr, out mCommandDelayMS))
                        {
                            if (mCommandDelayMS < 0) mCommandDelayMS = 0;
                        }
                    }
                    else if (item.StartsWith(pCommandUseShellExecute))
                    {
                        bool.TryParse(item.Substring(pCommandUseShellExecute.Length), out mCommandUseShellExecute);
                    }
                    else if (item.StartsWith(pRefreshNotificationURL))
                    {
                        mRefreshNotificationURL = item.Substring(pRefreshNotificationURL.Length);
                    }
                }
            }

            WatchFileOrDir(mDefaultFolderToWatch);
            mFileWatchers.ForEach(i => i.EnableRaisingEvents = true);

            Console.WriteLine(string.Format("Watching on folder: {0}", mDefaultFolderToWatch));
            Console.WriteLine("Press ENTER to stop watching.");
            Console.ReadLine();

            mFileWatchers.ForEach(i => { 
                i.EnableRaisingEvents = false;
                i.Dispose();
            });

        }

        private static void WatchFileOrDir(string dirOrFile)
        {
            if (Directory.Exists(dirOrFile))
            {
                DirectoryInfo di = new DirectoryInfo(dirOrFile);
                if (!mExcludeSubFolders.Contains(di.Name))
                {
                    FileSystemWatcher fs = new FileSystemWatcher(dirOrFile);
                    fs.NotifyFilter = NotifyFilters.LastWrite
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.Size
                                 | NotifyFilters.Security
                                 | NotifyFilters.Attributes;
                    fs.Changed += OnChanged;
                    fs.Created += OnChanged;
                    fs.Deleted += OnChanged;
                    fs.Renamed += OnRenamed;
                    mFileWatchers.Add(fs);

                    foreach (FileInfo fi in di.GetFiles())
                    {
                        WatchFileOrDir(fi.FullName);
                    }

                    foreach (DirectoryInfo subDi in di.GetDirectories())
                    {
                        WatchFileOrDir(subDi.FullName);
                    }
                }
            }
            else if (File.Exists(dirOrFile))
            {
                FileInfo fi = new FileInfo(dirOrFile);
                if (!mExcludeSubFolders.Contains(fi.Name))
                {
                    FileSystemWatcher fs = new FileSystemWatcher(fi.DirectoryName, fi.Name);
                    fs.NotifyFilter = NotifyFilters.LastWrite
                                 | NotifyFilters.FileName
                                 | NotifyFilters.Size
                                 | NotifyFilters.Security
                                 | NotifyFilters.Attributes;
                    fs.Changed += OnChanged;
                    fs.Created += OnChanged;
                    fs.Deleted += OnChanged;
                    fs.Renamed += OnRenamed;
                    mFileWatchers.Add(fs);
                }
            }
        }

        private static void OnChanged(object sender, FileSystemEventArgs e)
        {
            ChangeDetected();
        }

        private static void OnRenamed(object sender, RenamedEventArgs e)
        {
            ChangeDetected();
        }

        private static void ChangeDetected()
        {
            bool needExecution = false;

            lock (mFileWatchers)
            {
                if (mFileWatchers.Count > 0)
                {
                    needExecution = true;
                    mFileWatchers.ForEach(i =>
                    {
                        i.EnableRaisingEvents = false;
                        i.Changed -= OnChanged;
                        i.Created -= OnChanged;
                        i.Deleted -= OnChanged;
                        i.Renamed -= OnRenamed;
                        i.Dispose();
                    });
                    mFileWatchers.Clear();
                }
            }

            if (needExecution)
            {
                mExecutorThread = new Thread(new ThreadStart(ExecutorThreadMain));
                mExecutorThread.Name = "Command Execution Thread";
                mExecutorThread.IsBackground = true;
                mExecutorThread.Start();
            }
        }

        private static void ExecutorThreadMain()
        {
            Thread.Sleep(mCommandDelayMS);

            string[] cmdWithArgs = mCommandAtChange.Split(" ", StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < cmdWithArgs.Length; i++)
            {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append(cmdWithArgs[i]);
            }

            mExecutedProcess = new Process();
            mExecutedProcess.StartInfo = new ProcessStartInfo(cmdWithArgs[0], sb.ToString());
            mExecutedProcess.StartInfo.WorkingDirectory = mCommandWorkFolder;
            mExecutedProcess.StartInfo.UseShellExecute = mCommandUseShellExecute;
            //mExecutedProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            mExecutedProcess.StartInfo.RedirectStandardOutput = true;
            mExecutedProcess.StartInfo.RedirectStandardError = true;
            mExecutedProcess.OutputDataReceived += ExecutedProcess_OutputDataReceived;
            mExecutedProcess.ErrorDataReceived += ExecutedProcess_OutputDataReceived;
            mExecutedProcess.Start();
            mExecutedProcess.BeginOutputReadLine();
            mExecutedProcess.BeginErrorReadLine();
            mExecutedProcess.WaitForExit();
            mExecutedProcess = null;

            WatchFileOrDir(mDefaultFolderToWatch);
            mFileWatchers.ForEach(i => i.EnableRaisingEvents = true);

            if (!string.IsNullOrEmpty(mRefreshNotificationURL))
            {
                SendNotificationWebServerAsync();
            }
        }

        private static void ExecutedProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            Console.WriteLine(e.Data);
        }

        private static async void SendNotificationWebServerAsync()
        {
            Console.WriteLine($"Sending notification to the web server: {mRefreshNotificationURL}");
            using (var client = new ClientWebSocket())
            {
                try
                {
                    await client.ConnectAsync(new Uri(mRefreshNotificationURL), CancellationToken.None);
                }
                catch (Exception)
                {
                    Console.WriteLine($"Failed to connect web server: {mRefreshNotificationURL}");
                }
                if (client != null)
                {
                    try
                    {
                        await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("REFRESH")), WebSocketMessageType.Text, true, CancellationToken.None);
                        Console.WriteLine("Done.");
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Failed to send refresh signal.");
                    }
                }
            }
        }

    }

}
