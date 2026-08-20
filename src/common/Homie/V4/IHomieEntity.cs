namespace SmartHome.Homie.V4
{
    public interface IHomieEntity
    {
        public IHomieEntity? Parent { get; }

        public string TopicId { get; }

        public string GetTopic();

        public byte[] GetPayload();
    }
}
