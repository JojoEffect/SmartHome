using HomieNano.Version4.Attributes;
using HomieNano.Version4.Enums;
using HomieNano.Version4.EventArgs;
using Microsoft.Extensions.Logging;
using nanoFramework.Logging;
using System;
using System.Text;

namespace HomieNano.Version4.Properties
{
    public delegate void PropertyUpdateHandler(PropertyUpdateEventArgs args);

    public abstract class PropertyBase : NamedHomieEntityBase
    {
        private readonly DataTypeAttribute _dataTypeAttribute;
        private readonly FormatAttribute _formatAttribute;
        private readonly SettableAttribute _settableAttribute;
        private readonly RetainedAttribute _retainedAttribute;
        private readonly UnitAttribute _unitAttribute;
        private readonly ILogger _logger;

        protected PropertyBase(string topicId,
            string name,
            DataType dataType, 
            string format = "", 
            bool settable = false, 
            bool retained = true, 
            Unit unit = Unit.None) 
            : base(topicId, name)
        {
            _dataTypeAttribute = new(this, dataType);
            _formatAttribute = new(this, format);
            _settableAttribute = new(this, settable);
            _retainedAttribute = new(this, retained);
            _unitAttribute = new(this, unit);
            _logger = this.GetCurrentClassLogger();
        }


        /// <summary>
        /// Event raised when the property value is updated internally.
        /// </summary>
        public abstract event PropertyUpdateHandler? OnUpdate;

        public abstract override byte[] GetPayload();

        public DataTypeAttribute DataTypeAttribute => _dataTypeAttribute;
        
        public FormatAttribute FormatAttribute => _formatAttribute;
        
        public SettableAttribute SettableAttribute => _settableAttribute;
        
        public RetainedAttribute RetainedAttribute => _retainedAttribute;
        
        public UnitAttribute UnitAttribute => _unitAttribute;

        public void Set(byte[] value)
        {
            if (SettableAttribute.Value)
            {
                var str = Encoding.UTF8.GetString(value, 0, value.Length);
                _logger.LogDebug($"Setting property '{TopicId}' to value '{str}'.");
                SetInternal(str);
            }
            else
            {
                throw new InvalidOperationException($"Property '{TopicId}' is not settable.");
            }
        }

        internal abstract void SetInternal(string value);
    }
}
