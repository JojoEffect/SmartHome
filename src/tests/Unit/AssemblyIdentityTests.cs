using nanoFramework.TestFramework;

namespace SmartHome.UnitTests
{
    // The integration suite's [ITEST] markers name the test by reading the running
    // assembly's own name (IntegrationTest in src\integrationTests\TestSupport), so
    // that no project has to spell its name out a second time (issue #20). That rests
    // on one runtime API, and the integration suite cannot be run in CI to prove it.
    //
    // This suite can. It runs on the nanoclr virtual device in CI and on the ESP32
    // here, which are the two runtimes the marker has to work on -- and if reflection
    // were unavailable on either, every device-decided integration test would report
    // NO-RESULT with nothing in the log to say why.
    [TestClass]
    public class AssemblyIdentityTests
    {
        [TestMethod]
        public void A_Type_Knows_Its_Own_Assembly_By_Name()
        {
            // Not a tautology against a const: NFUnitTest is this project's
            // <AssemblyName>, which is deliberately not its project or namespace name
            // (nanoFramework.TestFramework's device-side launcher resolves the test
            // assembly by that exact name). So a value read off the runtime that
            // matches it can only have come from the assembly.
            string name = typeof(AssemblyIdentityTests).Assembly.GetName().Name;

            Assert.AreEqual("NFUnitTest", name);
        }

        [TestMethod]
        public void An_Assembly_Name_Is_The_Simple_Name_Not_The_Display_Name()
        {
            // FullName is "<name>, Version=x.y.z.w" on this runtime, and the marker
            // must carry only the part before the comma -- the runner compares it
            // against the project's <AssemblyName>, which has no version in it.
            string fullName = typeof(AssemblyIdentityTests).Assembly.FullName;
            string name = typeof(AssemblyIdentityTests).Assembly.GetName().Name;

            Assert.IsTrue(fullName.IndexOf(',') > 0, "FullName should carry a version after a comma: " + fullName);
            Assert.IsFalse(name.IndexOf(',') >= 0, "Name should not: " + name);
        }
    }
}
