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

        // The id rule lives in the model, in NamedEntityBase.ValidateId, which is what
        // actually runs -- an id is refused where it was written rather than on this
        // wire, and the model's rule is already at least as strict as v4's. A regex
        // sketch used to sit here and disagreed with the code -- it allowed '_',
        // required at least two characters, and forbade a trailing '_' -- leaving two
        // statements of the rule and no way to tell which was intended.
    }
}
