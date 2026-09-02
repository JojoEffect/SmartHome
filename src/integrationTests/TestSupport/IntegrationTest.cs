using System;
using System.Diagnostics;

namespace SmartHome.IntegrationTests.TestSupport
{
    /// <summary>
    /// Machine-readable pass/fail markers for the on-device integration tests.
    /// </summary>
    /// <remarks>
    /// A nanoFramework device app never "exits with a code" the way a desktop test
    /// process does -- it runs until it's reflashed or reset. So the suite runner
    /// (scripts\Run-IntegrationTests.ps1) decides the outcome by reading the device's
    /// managed debug output and matching these exact prefixes. Keep them stable:
    /// changing the strings here means changing the regex in that script too.
    /// <para>
    /// The name in the marker is the test's own assembly name, read off the running
    /// assembly rather than spelled out. The runner expects the &lt;AssemblyName&gt;
    /// of the project it just flashed, so the two cannot drift: there is one name,
    /// in the .nfproj, and both ends derive from it. It used to be a per-project
    /// <c>TestName</c> const, which was a third independent spelling and cost the
    /// runner a whole outcome to police (issue #20).
    /// </para>
    /// </remarks>
    public static class IntegrationTest
    {
        /// <summary>Prefix every marker line starts with.</summary>
        public const string Marker = "[ITEST]";

        /// <summary>Reports the test as passed. Emit as soon as the outcome is known.</summary>
        /// <param name="testType">Any type from the test's own assembly -- pass <c>typeof(Program)</c>.</param>
        /// <param name="detail">Free text the runner prints verbatim.</param>
        public static void Pass(Type testType, string detail = null)
        {
            string name = NameOf(testType);

            Debug.WriteLine(detail == null
                ? $"{Marker} {name} PASS"
                : $"{Marker} {name} PASS: {detail}");
        }

        /// <summary>Reports the test as failed, with a reason the runner prints verbatim.</summary>
        /// <param name="testType">Any type from the test's own assembly -- pass <c>typeof(Program)</c>.</param>
        /// <param name="reason">Why it failed. Printed verbatim.</param>
        public static void Fail(Type testType, string reason)
        {
            Debug.WriteLine($"{Marker} {NameOf(testType)} FAIL: {reason}");
        }

        /// <summary>
        /// The assembly the caller's type lives in, by name.
        /// </summary>
        /// <remarks>
        /// The type has to come from the caller: this class ships two ways --
        /// referenced from TestSupport (WifiCheck, MqttCheck) and compiled in as a
        /// linked file (Bmp280Check) -- so Assembly.GetExecutingAssembly() here would
        /// name TestSupport for some tests and the test itself for others.
        /// </remarks>
        private static string NameOf(Type testType)
        {
            return testType.Assembly.GetName().Name;
        }
    }
}
