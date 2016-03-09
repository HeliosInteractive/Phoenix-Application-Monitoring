﻿namespace phoenix
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Diagnostics;
    using System.Threading.Tasks;
    using Microsoft.VisualBasic.Devices;

    class ProcessRunner : IDisposable
    {
        #region Private Members

        PerformanceCounter      m_PerfCounter   = null;
        Process                 m_Process       = null;
        string                  m_WorkingDir    = string.Empty;
        string                  m_StartScript   = string.Empty;
        string                  m_CrashScript   = string.Empty;
        string                  m_ProcessPath   = string.Empty;
        string                  m_CommandLine   = string.Empty;
        string                  m_CachedName    = string.Empty;
        string                  m_Environment   = string.Empty;
        double[]                m_MemoryUsage   = new double[m_NumSamples];
        double[]                m_CpuUsage      = new double[m_NumSamples];
        double[]                m_UsageIndices  = new double[m_NumSamples];
        double                  m_MaxMemory     = 1d;
        double                  m_LastCpuUsage  = 0d,
                                m_LastMemUsage  = 0d;
        const int               m_NumSamples    = 100;
        int                     m_DelaySeconds  = 0;
        int                     m_WaitTime      = 0;
        bool                    m_AlwaysOnTop   = false;
        bool                    m_CrashIfUnresp = false;
        bool                    m_Monitoring    = false;
        bool                    m_CaptureOutput = false;
        public enum ExecType    { CRASHED, NORMAL }

        #endregion

        #region Property Indexers

        public double[] UsageIndices    { get { return m_UsageIndices; } }
        public double[] MemoryUsage     { get { return m_MemoryUsage; } }
        public double[] CpuUsage        { get { return m_CpuUsage; } }
        public int      NumSamples      { get { return m_NumSamples; } }
        public bool     Monitoring      { get { return m_Monitoring; } }
        public double   LastCpuUsage    { get { return Monitoring ? m_LastCpuUsage : 0d; } }
        public double   LastMemUsage    { get { return Monitoring ? m_LastMemUsage : 0d; } }
        public string   Environment     { get; set; }
        public bool CaptureConsoleOutput
        {
            get { return m_CaptureOutput; }
            set { m_CaptureOutput = value; }
        }
        public bool AssumeCrashIfNotResponsive
        {
            get { return m_CrashIfUnresp; }
            set { m_CrashIfUnresp = value; }
        }
        public bool ForceAlwaysOnTop
        {
            get { return m_AlwaysOnTop; }
            set { m_AlwaysOnTop = value; }
        }
        public string CrashScript
        {
            get { return m_CrashScript; }
            set { m_CrashScript = value; Validate(); }
        }
        public string StartScript
        {
            get { return m_StartScript; }
            set { m_StartScript = value; Validate(); }
        }
        public string WorkingDirectory
        {
            get { return m_WorkingDir; }
            set { m_WorkingDir = value; Validate(); }
        }
        public int DelaySeconds
        {
            get { return m_DelaySeconds; }
            set { m_DelaySeconds = value; Validate(); }
        }
        public int WaitTime
        {
            get { return m_WaitTime; }
            set { m_WaitTime = value; Validate(); }
        }
        public string ProcessPath
        {
            get { return m_ProcessPath; }
            set { m_ProcessPath = value; Validate(); }
        }
        public string CommandLine
        {
            get { return m_CommandLine; }
            set { m_CommandLine = value; Validate(); }
        }
        public string CachedTitle
        {
            get { return m_CachedName; }
            set { m_CachedName = value; }
        }

        #endregion

        #region Events

        public Action<ExecType> ProcessStarted;
        public Action<ExecType> ProcessStopped;

        void OnProcessStarted(ExecType type)
        {
            ResetPerformanceCounter();

            m_Monitoring = true;
            m_CachedName = m_Process.ProcessName;

            if (ProcessStarted != null)
                ProcessStarted(type);
        }

        void OnProcessStopped(ExecType type)
        {
            m_Monitoring = false;

            if (type == ExecType.CRASHED)
            {
                CallScript(m_CrashScript);

                Task.Delay(new TimeSpan(0, 0, DelaySeconds))
                    .ContinueWith((fn) => {
                        if (!m_Monitoring)
                            Start(ExecType.CRASHED);
                    });
            }

            if (ProcessStopped != null)
                ProcessStopped(type);
        }

        #endregion

        public ProcessRunner()
        {
            for (int index = 0; index < m_NumSamples; ++index)
            {
                m_UsageIndices[index] = index;
                m_MemoryUsage[index] = 0;
                m_CpuUsage[index] = 0;
            }

            try { m_MaxMemory = new ComputerInfo().AvailablePhysicalMemory; }
            catch { m_MaxMemory = -1d; }
        }

        // This is here solely because I need to remove this
        // subscriber in case of a NORMAL Stop() request.
        void OnProcessCrashed(object sender, EventArgs e)
        { OnProcessStopped(ExecType.CRASHED); }

        public void Stop(ExecType type)
        {
            if (m_Process != null)
            {
                // do not raise events if we are stopping
                m_Process.EnableRaisingEvents = false;
                m_Process.Exited -= OnProcessCrashed;

                try
                {
                    // Step 1. Ask window to close by sending WM_CLOSE
                    if (HasMainWindow())
                    {
                        m_Process.CloseMainWindow();
                        m_Process.WaitForExit(1000);
                    }

                    // Step 2. Ask C# Kill API to handle it
                    if (!m_Process.HasExited)
                    {
                        Logger.ProcessRunner.Warn("Failed to close with WM_CLOSE");
                        m_Process.Kill();
                        m_Process.WaitForExit(1000);
                    }

                    // Step 3. task kill that mo-fo, fo-sho
                    if (!m_Process.HasExited)
                    {
                        Logger.ProcessRunner.Warn("Failed to close with Kill API");
                        ExecuteScript("taskkill",
                            string.Format("/F /T /IM {0}.exe", m_Process.ProcessName));
                    }

                    m_Process.Dispose();
                    m_Process = null;
                }
                catch(Exception ex)
                {
                    Logger.ProcessRunner.ErrorFormat("Error shutting down the process: {0}",
                        ex.Message);
                }
            }

            if (!String.IsNullOrWhiteSpace(m_CachedName))
            {
                if (Process.GetProcessesByName(m_CachedName).Length > 0)
                {
                    Logger.ProcessRunner.Warn("A leftover process found with the cached name.");

                    ExecuteScript("taskkill",
                        string.Format("/F /T /IM {0}.exe", m_CachedName));
                }
            }

            OnProcessStopped(type);
        }

        public void Start(ExecType type)
        {
            if (!Validate() || m_Disposed)
                return;

            Stop(ExecType.NORMAL);
            CallScript(m_StartScript);

            m_Process = new Process {
                StartInfo = new ProcessStartInfo
                {
                    WorkingDirectory        = WorkingDirectory,
                    UseShellExecute         = false,
                    Arguments               = CommandLine,
                    FileName                = ProcessPath,
                    RedirectStandardOutput  = CaptureConsoleOutput,
                    RedirectStandardError   = CaptureConsoleOutput,
                },
                EnableRaisingEvents = true,
            };

            if (!String.IsNullOrWhiteSpace(Environment))
            {
                foreach (string variable_expr in Environment
                    .Split(new[] { "\r\n" },
                        StringSplitOptions.RemoveEmptyEntries))
                {
                    string key = string.Empty;
                    string val = string.Empty;

                    string[] split_expr = variable_expr.Split('=');
                    if (split_expr.Length >= 1)
                    {
                        key = split_expr[0].Trim();
                        if (split_expr.Length > 1)
                        {
                            val = String.Join("", split_expr.Skip(1)).Trim();
                            if (!String.IsNullOrWhiteSpace(val))
                                val = System.Environment.ExpandEnvironmentVariables(val);
                        }
                    }

                    if (!String.IsNullOrWhiteSpace(key))
                        m_Process.StartInfo.EnvironmentVariables[key] = val;
                }
            }

            if (CaptureConsoleOutput)
            {
                m_Process.OutputDataReceived += (s, e) => {
                    if (!String.IsNullOrEmpty(e.Data))
                        Logger.ProcessRunner.InfoFormat("stdout: {0}", e.Data);
                };
                m_Process.ErrorDataReceived += (s, e) => {
                    if (!String.IsNullOrEmpty(e.Data))
                        Logger.ProcessRunner.ErrorFormat("stderr: {0}", e.Data);
                };
            }

            try
            {
                m_Process.Exited += OnProcessCrashed;
                m_Process.Start();

                if (HasMainWindow())
                    m_Process.WaitForInputIdle(5000);

                if (CaptureConsoleOutput)
                {
                    m_Process.BeginOutputReadLine();
                    m_Process.BeginErrorReadLine();
                }

                if (m_Process.Responding)
                    OnProcessStarted(type);
            }
            catch(Exception ex)
            {
                Logger.ProcessRunner.ErrorFormat("Unable to start the process: {0}"
                    , ex.Message);
                Stop(ExecType.NORMAL);
            }
        }

        public void Monitor()
        {
            if (!Monitorable())
                return;

            m_Process.Refresh();

            if (AssumeCrashIfNotResponsive && !m_Process.Responding)
                Task.Delay(new TimeSpan(0, 0, WaitTime))
                    .ContinueWith((fn) => {
                        if (!m_Process.Responding)
                            Stop(ExecType.CRASHED);
                    });

            if (ForceAlwaysOnTop && HasMainWindow())
            {
                if (NativeMethods.GetForegroundWindow() !=
                    m_Process.MainWindowHandle)
                {
                    NativeMethods.SwitchToThisWindow(m_Process.MainWindowHandle, true);
                    NativeMethods.SetForegroundWindow(m_Process.MainWindowHandle);
                    NativeMethods.SetWindowPos(
                        m_Process.MainWindowHandle,
                        NativeMethods.HWND_TOPMOST,
                        0, 0, 0, 0,
                        NativeMethods.SetWindowPosFlags.SWP_NOSIZE |
                        NativeMethods.SetWindowPosFlags.SWP_NOMOVE |
                        NativeMethods.SetWindowPosFlags.SWP_SHOWWINDOW);
                }
            }
        }

        public void UpdateMetrics()
        {
            if (!Monitorable())
                return;

            for (int index = 1; index < m_NumSamples; ++index)
            {
                m_MemoryUsage[index - 1] = m_MemoryUsage[index];
                m_CpuUsage[index - 1] = m_CpuUsage[index];
            }

            int sample_index = m_NumSamples - 1;
            m_MemoryUsage[sample_index] = m_Process.WorkingSet64 / m_MaxMemory;
            try { m_CpuUsage[sample_index] = m_PerfCounter.NextValue() / (System.Environment.ProcessorCount * 100d); }
            catch { m_CpuUsage[sample_index] = 0; }

            m_LastCpuUsage = m_CpuUsage[sample_index];
            m_LastMemUsage = m_MemoryUsage[sample_index];
        }

        void ResetPerformanceCounter()
        {
            if (!Monitorable())
                return;

            if (m_PerfCounter != null)
                m_PerfCounter.Close();

            m_PerfCounter = new PerformanceCounter(
                "Process",
                "% Processor Time",
                m_Process.ProcessName,
                m_Process.MachineName);
        }

        bool Monitorable()
        {
            try { return m_Process != null && !m_Process.HasExited; }
            catch { return false; }
        }

        bool HasMainWindow()
        {
            try { return Monitorable() && m_Process.MainWindowHandle != IntPtr.Zero; }
            catch { return false; }
        }

        bool Validate()
        {
            m_WaitTime      = Math.Abs(m_WaitTime);
            m_DelaySeconds  = Math.Abs(m_DelaySeconds);
            m_ProcessPath   = m_ProcessPath.AsPath();
            m_CrashScript   = m_CrashScript.AsPath();
            m_StartScript   = m_StartScript.AsPath();
            m_WorkingDir    = m_WorkingDir.AsPath();

            if (m_ProcessPath != string.Empty && !Path.IsPathRooted(m_ProcessPath))
                m_ProcessPath = Path.GetFullPath(m_ProcessPath);

            if (m_CrashScript != string.Empty && !Path.IsPathRooted(m_CrashScript))
                m_CrashScript = Path.GetFullPath(m_CrashScript);

            if (m_StartScript != string.Empty && !Path.IsPathRooted(m_StartScript))
                m_StartScript = Path.GetFullPath(m_StartScript);

            if (m_WorkingDir != string.Empty && !Path.IsPathRooted(m_WorkingDir))
                m_WorkingDir = Path.GetFullPath(m_WorkingDir);

            if (!File.Exists(m_ProcessPath) || Path.GetExtension(m_ProcessPath) != ".exe") {
                m_ProcessPath = string.Empty;
                return false;
            }

            if (!File.Exists(m_CrashScript)) {
                m_CrashScript = string.Empty;
            }

            if (!File.Exists(m_StartScript)) {
                m_StartScript = string.Empty;
            }

            if (!Directory.Exists(m_WorkingDir)) {
                m_WorkingDir = string.Empty;
            }

            return true;
        }

        static void CallScript(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return;

            using (Process process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = path
                }
            })
            {
                process.Start();
                process.WaitForExit();
            }
        }

        static void ExecuteScript(string exe, string cmd)
        {
            if (String.IsNullOrWhiteSpace(exe) ||
                String.IsNullOrWhiteSpace(cmd))
                return;

            using (Process process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = exe,
                    Arguments = cmd,
                    CreateNoWindow = true,
                    UseShellExecute = true,
                }
            })
            {
                process.Start();
                process.WaitForExit();
            }
        }

        #region IDisposable Support
        private bool m_Disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!m_Disposed)
            {
                if (disposing)
                {
                    if (m_PerfCounter != null)
                        m_PerfCounter.Close();

                    if (m_Process != null)
                        m_Process.Close();
                }
                // free native resources
                m_Disposed = true;
            }
        }

        public void Dispose()
        {
            Stop(ExecType.NORMAL);
            Dispose(true);
        }
        #endregion
    }
}
