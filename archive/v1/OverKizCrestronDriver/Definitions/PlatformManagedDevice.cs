#region Copyright

// ---------------------------------------------------------------------------
// Copyright © 2023 to the present, Crestron Electronics®, Inc.
// All Rights Reserved.
// No part of this software may be reproduced in any form, machine
// or natural, without the express written consent of Crestron Electronics.
// Use of this source code is subject to the terms of the Crestron Software
// License Agreement under which you licensed this source code.
// ---------------------------------------------------------------------------

#endregion

using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace OverKiz.CrestronDriver
{
    [EntityDataType(Id = "platform:ManagedDevice")]
    public class PlatformManagedDevice
    {
        public PlatformManagedDevice(
            DeviceUxCategory uxCategory,
            string name,
            string manufacturer,
            string model,
            string serialNumber
        )
        {
            UxCategory = uxCategory;
            Name = name;
            Manufacturer = manufacturer;
            Model = model;
            SerialNumber = serialNumber;
        }

        [EntityProperty]
        public DeviceUxCategory UxCategory { get; private set; }

        [EntityProperty]
        public string Name { get; private set; }

        [EntityProperty]
        public string Manufacturer { get; private set; }

        [EntityProperty]
        public string Model { get; private set; }

        [EntityProperty]
        public string SerialNumber { get; private set; }
    }
}