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
    /// </remarks>
    public static class IntegrationTest
    {
        /// <summary>Prefix every marker line starts with.</summary>
        public const string Marker = "[ITEST]";

        /// <summary>Reports the test as passed. Emit as soon as the outcome is known.</summary>
        public static void Pass(string testName, string detail = null)
        {
            Debug.WriteLine(detail == null
                ? $"{Marker} {testName} PASS"
                : $"{Marker} {testName} PASS: {detail}");
        }

        /// <summary>Reports the test as failed, with a reason the runner prints verbatim.</summary>
        public static void Fail(string testName, string reason)
        {
            Debug.WriteLine($"{Marker} {testName} FAIL: {reason}");
        }
    }
}
