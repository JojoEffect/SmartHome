using System.Text;

namespace SmartHome.Homie.V4.Description
{
    /// <summary>
    /// One <c>$</c>-attribute, ready to publish: its topic and its encoded payload.
    /// </summary>
    /// <remarks>
    /// Both are fixed once the device is built -- an attribute describes the device's
    /// shape, and the model's tree cannot change after it is built -- so both are
    /// rendered once, at construction, rather than on every announce. An announce
    /// re-runs on every reconnect, and it is one of these per attribute of every node
    /// and property.
    ///
    /// <c>$state</c> is the exception and is not one of these: it is the only attribute
    /// whose payload moves while the device runs.
    /// </remarks>
    internal sealed class HomieAttributeMessage
    {
        internal HomieAttributeMessage(string topic, string payload)
        {
            Topic = topic;
            Payload = Encoding.UTF8.GetBytes(payload);
        }

        internal string Topic { get; }

        internal byte[] Payload { get; }
    }
}
