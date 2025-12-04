namespace HomieNano.Version4
{
    public interface IHomieEntity
    {
        public IHomieEntity? Parent { get; }

        public string TopicId { get; }

        public string GetTopic();

        public string GetPayload();
    }
}
