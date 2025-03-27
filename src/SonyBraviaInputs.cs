using PepperDash.Core;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Core.Queues;
using System;
using System.Collections.Generic;

namespace Pepperdash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaInputs : ISelectableItems<string>
    {
        private Dictionary<string, ISelectableItem> _items = new Dictionary<string, ISelectableItem>();

        public Dictionary<string, ISelectableItem> Items
        {
            get
            {
                return _items;
            }
            set
            {
                if (_items == value)
                    return;

                _items = value;

                ItemsUpdated?.Invoke(this, null);
            }
        }

        private string _currentItem;

        public string CurrentItem
        {
            get
            {
                return _currentItem;
            }
            set
            {
                if (_currentItem == value)
                    return;

                _currentItem = value;

                CurrentItemChanged?.Invoke(this, null);
            }
        }

        public event EventHandler ItemsUpdated;
        public event EventHandler CurrentItemChanged;

    }

    public class SonyBraviaInput : ISelectableItem
    {
        private bool _isSelected;

        private readonly byte[] _command;

        private readonly IQueueMessage _inputCommand;
        private readonly SonyBraviaDevice _parent;

        public SonyBraviaInput(string key, string name, SonyBraviaDevice parent, IQueueMessage inputCommand)
        {
            Key = key;
            Name = name;
            _parent = parent;
            _inputCommand = inputCommand;
        }

        public SonyBraviaInput(string key, string name, SonyBraviaDevice parent, byte[] command)
        {
            Key = key;
            Name = name;
            _command = command;
            _parent = parent;
        }

        public string Key { get; private set; }
        public string Name { get; private set; }

        public event EventHandler ItemUpdated;

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (value == _isSelected)
                    return;

                _isSelected = value;
                var handler = ItemUpdated;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }
        }

        public void Select()
        {
            if (_parent.ComsIsRs232)
            {
                Debug.LogMessage(Serilog.Events.LogEventLevel.Information, "Sending input command for {name}: {command}", this, Name, ComTextHelper.GetEscapedText(_command));

                _parent.SendRs232Command(_command);

                return;
            }

            _parent.EnqueueCommand(_inputCommand);
        }
    }
}
