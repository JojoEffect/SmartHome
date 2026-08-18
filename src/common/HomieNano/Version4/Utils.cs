using HomieNano.Version4.Enums;

namespace HomieNano.Version4
{
    public static class Utils
    {
        public static string[] GetTopicIds(IHomieEntity[] entities)
        {
            string[] entityTopicIds = new string[entities.Length];

            for (int i = 0; i < entities.Length; i++)
            {
                entityTopicIds[i] = entities[i].TopicId;
            }

            return entityTopicIds;
        }
    }
}
