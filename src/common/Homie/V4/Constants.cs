namespace SmartHome.Homie.V4
{
    public class Constants
    {
        public const string Version4 = "4";
        public const string TopicSeparator = "/";
        public const string RootTopicId = "homie";
        public const string SetPropertyTopicId = "set";
        public const string AttributeIdentifierPrefix = "$";
        public const string HomieAttributeTopicId = "$homie";
        public const string NameAttributeTopicId = "$name";
        public const string NodesAttributeTopicId = "$nodes";
        public const string StateAttributeTopicId = "$state";
        public const string ExtensionAttributeTopicId = "$extensions";
        public const string ImplementationAttributeTopicId = "$implementation";
        public const string TypeAttributeTopicId = "$type";
        public const string PropertiesAttributeTopicId = "$properties";
        public const string DataTypeAttributeTopicId = "$datatype";
        public const string FormatAttributeTopicId = "$format";
        public const string SettableAttributeTopicId = "$settable";
        public const string RetainedAttributeTopicId = "$retained";
        public const string UnitAttributeTopicId = "$unit";
        //Regex ^[a-z0-9]+[a-z0-9\\-_]*[^-_]$
    }
}
