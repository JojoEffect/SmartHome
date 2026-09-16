using nanoFramework.M2Mqtt;
using System.Security.Cryptography.X509Certificates;

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
        public MqttClientTransport(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol)
            : base(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol)
        {
        }
    }
}
