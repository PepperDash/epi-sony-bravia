using System;
using System.Collections.Generic;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;

namespace PepperDash.Essentials.Plugins.SonyBravia
{
    public class SonyBraviaIrInputs : ISelectableItems<string>
    {
        private Dictionary<string, ISelectableItem> _items = new Dictionary<string, ISelectableItem>();

        public Dictionary<string, ISelectableItem> Items
        {
            get { return _items; }
            set
            {
                if (_items == value)
                    return;

                _items = value;
                ItemsUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        private string _currentItem;

        public string CurrentItem
        {
            get { return _currentItem; }
            set
            {
                if (_currentItem == value)
                    return;

                _currentItem = value;
                CurrentItemChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler ItemsUpdated;
        public event EventHandler CurrentItemChanged;
    }

    public class SonyBraviaIrInput : ISelectableItem
    {
        private readonly SonyBraviaIrController _parent;
        private bool _isSelected;

        public SonyBraviaIrInput(string key, string name, SonyBraviaIrController parent)
        {
            Key = key;
            Name = name;
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
                ItemUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Select()
        {
            _parent.LogDebug("Input '{key}' selected", Key);
            _parent.ExecuteSwitch(Key);
        }
    }
}
