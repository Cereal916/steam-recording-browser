using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace SteamRecordingBrowser.Tests.Ux;

internal static class PrivateDesktopProcess
{
    private const string DesktopPrefix = "SteamRecordingBrowser.Tests.";
    // No DESKTOP_SWITCHDESKTOP or hook/journal rights are requested.
    private const uint DesktopAccess = 0x0001 | 0x0002 | 0x0004 | 0x0040 | 0x0080;

    public static bool IsWorker
    {
        get
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2,
                    name, name.Capacity * sizeof(char), out _))
                throw NativeError("Read test desktop");
            return name.ToString().StartsWith(DesktopPrefix, StringComparison.Ordinal);
        }
    }

    public static string ArtifactDirectory
    {
        get
        {
            var path = Path.GetFullPath(Environment.GetEnvironmentVariable("SRB_UX_ARTIFACTS")
                ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "ux"));
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static Task RunCurrentTest()
    {
        var testId = TestContext.Current.TestCase?.UniqueID
            ?? throw new InvalidOperationException("The UX host must run inside an xUnit test case.");
        return Task.Run(() => RunChild(testId));
    }

    private static void RunChild(string testId)
    {
        var name = DesktopPrefix + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, DesktopAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw NativeError("Create isolated desktop");

        ProcessInformation process = default;
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "SteamRecordingBrowser.Tests.exe");
            var resultPath = Path.Combine(ArtifactDirectory, $"result-{Guid.NewGuid():N}.xml");
            // xunit.runner.json enumerates theory rows so each ID selects exactly one scenario.
            var arguments = new StringBuilder($"{QuoteArgument(executable)} -id {testId} -noLogo -result-xml {QuoteArgument(resultPath)}");
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = name,
                Flags = 0x00000080 // STARTF_FORCEOFFFEEDBACK: no startup cursor changes.
            };
            // CREATE_NO_WINDOW suppresses a console. lpDesktop isolates all native WPF windows
            // from process startup; no SwitchDesktop, SendInput, or interactive fallback exists.
            if (!CreateProcess(executable, arguments, IntPtr.Zero, IntPtr.Zero, false,
                    0x08000000, IntPtr.Zero, Environment.CurrentDirectory, ref startup, out process))
                throw NativeError("Start isolated xUnit process");

            var wait = WaitForSingleObject(process.Process, 45000);
            if (wait != 0)
            {
                TerminateProcess(process.Process, 1);
                WaitForSingleObject(process.Process, 5000);
                throw new TimeoutException("The isolated UX test process did not exit within 45 seconds.");
            }
            if (!GetExitCodeProcess(process.Process, out var exitCode))
                throw NativeError("Read isolated test result");
            if (!File.Exists(resultPath))
                throw new InvalidOperationException($"The isolated test exited with code {exitCode} without a report.");

            var report = XDocument.Load(resultPath);
            var tests = report.Descendants("test").ToArray();
            if (exitCode != 0 || tests.Length != 1 || (string?)tests[0].Attribute("result") != "Pass")
            {
                var failures = string.Join(Environment.NewLine, report.Descendants("failure")
                    .Select(failure => $"{failure.Element("message")?.Value}\n{failure.Element("stack-trace")?.Value}"));
                throw new InvalidOperationException($"Isolated UX test failed (exit {exitCode}, {tests.Length} cases).\n{failures}\nReport: {resultPath}");
            }
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            CloseDesktop(desktop);
        }
    }

    private static Win32Exception NativeError(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation} failed ({code}: {new Win32Exception(code).Message}). " +
            "No UI will be opened on the interactive desktop.");
    }

    private static string QuoteArgument(string value)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            quoted.Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode,
        uint flags, uint access, IntPtr security);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index,
        StringBuilder information, int length, out int needed);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startup,
        out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
