using nanoFramework.M2Mqtt;
using System.Security.Cryptography.X509Certificates;

// File-local, because this project does not set <Nullable>enable</Nullable> (issue #127).
// Without it the certificate parameters below could only be spelled unannotated, which
// says nothing at all about whether null is allowed -- and the caller one file over hands
// them null whenever `secure` is false. Annotating them in a disabled context would be
// worse than saying nothing: the `?` would be accepted, enforce nothing, and add two more
// CS8632 warnings to the eight that issue already tracks. So the context is turned on for
// the one file that needs it rather than for a project whose existing annotations are not
// ready to become binding.
#nullable enable

namespace SmartHome.Mqtt
{
    /// <summary>
    /// The real transport: M2Mqtt's own <see cref="MqttClient"/>, seen through
    /// <see cref="IMqttTransport"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no forwarding code here on purpose. <see cref="MqttClient"/> is a plain
    /// public class rather than a sealed one, and every member
    /// <see cref="IMqttTransport"/> asks for -- <c>IsConnected</c>, <c>Init</c>,
    /// <c>Connect</c>, <c>Disconnect</c>, <c>Close</c>, <c>Publish</c>,
    /// <c>Subscribe</c>, <c>Unsubscribe</c>, and all six events -- is already public on
    /// it, so declaring the interface on a subclass is the whole implementation. A
    /// hand-written adapter holding an <see cref="MqttClient"/> and forwarding fifteen
    /// members would be ~200 lines that can drift from the client it forwards to; this
    /// cannot.
    /// </para>
    /// <para>
    /// A subclass is also why nothing had to change about how the client is constructed:
    /// <see cref="MqttClient"/>'s constructor only assigns fields and creates wait
    /// handles -- no DNS, no socket -- so building one costs nothing and reaches no
    /// network, on a device or under a test runner.
    /// </para>
    /// </remarks>
    public sealed class MqttClientTransport : MqttClient, IMqttTransport
    {
        /// <inheritdoc cref="MqttClient(string)"/>
        public MqttClientTransport(string brokerHostName)
            : base(brokerHostName)
        {
        }

        /// <inheritdoc cref="MqttClient(string, int, bool, X509Certificate, X509Certificate, MqttSslProtocols)"/>
        /// <param name="caCert">
        /// The CA certificate, or null. Null whenever <paramref name="secure"/> is false,
        /// which is how every caller in this repository connects.
        /// </param>
        /// <param name="clientCert">The client certificate, or null. Same as above.</param>
        public MqttClientTransport(string brokerHostName, int brokerPort, bool secure, X509Certificate? caCert, X509Certificate? clientCert, MqttSslProtocols sslProtocol)
            : base(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol)
        {
        }
    }
}
