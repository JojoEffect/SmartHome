using SmartHome.Homie.V4.Enums;

namespace SmartHome.Homie.V4
{
    public static class TopicIds
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
