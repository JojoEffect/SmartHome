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

        // The topic-id rule lives in NamedHomieEntityBase.ValidateTopicId, which is what
        // actually runs. A regex sketch used to sit here and disagreed with it -- it
        // allowed '_', required at least two characters, and forbade a trailing '_' --
        // leaving two statements of the rule and no way to tell which was intended.
    }
}
