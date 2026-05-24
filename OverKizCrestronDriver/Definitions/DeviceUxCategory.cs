// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace OverKiz.CrestronDriver
{
    [EntityDataType(Id = "crestron:DeviceUxCategory")]
    public enum DeviceUxCategory
    {
        /// <summary>
        ///     The device did not fit into any of the predefined categories
        /// </summary>
        Other,

        /// <summary>
        ///     The device is primarily an audio amplifier
        /// </summary>
        Amplifier,

        /// <summary>
        ///     The device is primarily an appliance, such as a stove, oven, coffee maker,
        ///     microwave, dishwasher, or similar device.
        /// </summary>
        Appliance,

        /// <summary>
        ///     The device represents an area, rolling up any states and control for the devices and
        ///     rooms in the area to an area-level API.
        ///     An area is defined as a space (physical or virtual) that may be stateful and
        ///     controllable as a whole, composed of devices, rooms, and nested areas (which may or
        ///     may not be listed) that belong to that area. A device, room, or area may only be
        ///     directly contained by one parent area. A hierarchy of areas, rooms, and devices is
        ///     like a tree structure where each node has a single parent.
        /// </summary>
        Area,

        /// <summary>
        ///     The device is primarily an audio mixer
        /// </summary>
        AudioMixer,

        /// <summary>
        ///     The device is primarily an audio processor / pre-amp
        /// </summary>
        AudioProcessor,

        /// <summary>
        ///     The device is primarily an Audio/Video Receiver (AVR)
        /// </summary>
        AvReceiver,

        /// <summary>
        ///     The device is primarily an Audio/Video Switcher
        /// </summary>
        AvSwitcher,

        /// <summary>
        ///     The device is primarily an Audio Switcher
        /// </summary>
        AudioSwitcher,

        /// <summary>
        ///     The device is primarily a Bluray player
        /// </summary>
        BlurayPlayer,

        /// <summary>
        ///     The device is primarily a Cable Box (as in the cable signal receiver for a TV, not a
        ///     storage crate for wires)
        /// </summary>
        CableBox,

        /// <summary>
        ///     The device is primarily a camera, usually with PTZ (pan, tilt, zoom) capabilities
        /// </summary>
        Camera,

        /// <summary>
        ///     The device is primarily a video display device
        /// </summary>
        Display,

        /// <summary>
        ///     The device is primarily a fan
        /// </summary>
        Fan,

        /// <summary>
        ///     The device is primarily a fireplace
        /// </summary>
        Fireplace,

        /// <summary>
        ///     The device is primarily a video game console, such as an XBOX, PlayStation, or
        ///     Nintendo.
        /// </summary>
        GameConsole,

        /// <summary>
        ///     The device is primarily a garage door
        /// </summary>
        GarageDoor,

        /// <summary>
        ///     The device represents a group of entites, rolling up any states and control for
        ///     members of the group to a group-level API.
        ///     Groups may overlap, contain other groups, and contain devices from various rooms and
        ///     areas.
        /// </summary>
        Group,

        /// <summary>
        ///     The device is primarily an HVAC device, like a thermostat, heating / air
        ///     conditioning system, humidifier, dehumidifier, or similar device.
        /// </summary>
        Hvac,

        /// <summary>
        ///     The device is primarily an intercom system
        /// </summary>
        Intercom,

        /// <summary>
        ///     The device is primarily an irrigation system, such as a lawn sprinkler system or
        ///     garden irrigation system
        /// </summary>
        IrrigationSystem,

        /// <summary>
        ///     The device is primarily a lighting device
        /// </summary>
        Light,

        /// <summary>
        ///     The device is primarily a lock, such as a door lock or other electronic lock
        /// </summary>
        Lock,

        /// <summary>
        ///     The device is primarily a network router device
        /// </summary>
        NetworkRouter,

        /// <summary>
        ///     The device is primarily a network (ethernet) switch
        /// </summary>
        NetworkSwitch,

        /// <summary>
        ///     The device is primarily a smart power outlet / smart plug
        /// </summary>
        Outlet,

        /// <summary>
        ///     The device is primarily a "platform" for other devices, like a gateway to another
        ///     ecosystem of devices
        /// </summary>
        Platform,

        /// <summary>
        ///     The device is a pool, typically contained in and controlled by a pool controller
        ///     device. This device is the representation of the pool itself.
        /// </summary>
        Pool,

        /// <summary>
        ///     The device is a controller for a pool system, typically containing a pool and/or spa
        ///     device, as well as other devices
        /// </summary>
        PoolController,

        /// <summary>
        ///     The device is primarily a power controller / power distribution unit (PDU)
        /// </summary>
        PowerController,

        /// <summary>
        ///     The device is primarily a document printer
        /// </summary>
        Printer,

        /// <summary>
        ///     The device is primarily a video projector device
        /// </summary>
        Projector,

        /// <summary>
        ///     The device is primarily a lift for a video projector
        /// </summary>
        ProjectorLift,

        /// <summary>
        ///     The device is primarily a projector screen
        /// </summary>
        ProjectorScreen,

        /// <summary>
        ///     The device represents a room, rolling up any states and control for the devices in
        ///     the room to a room-level API.
        ///     A room is defined as a space (physical or virtual) that may be stateful and
        ///     controllable as a whole, composed of devices (which may or may not be listed by the
        ///     room) that belong to that room. A device may only be in one room, and a room may not
        ///     contain other rooms.
        /// </summary>
        Room,

        /// <summary>
        ///     The device is primarily a document scanner
        /// </summary>
        Scanner,

        /// <summary>
        ///     The device is primarily a security system
        /// </summary>
        SecuritySystem,

        /// <summary>
        ///     The device is primarily a sensor device, such as a door sensor, occupancy sensor,
        ///     light sensor, etc.
        /// </summary>
        Sensor,

        /// <summary>
        ///     The device is primarily a window shade, curtain, drapes, etc.
        /// </summary>
        Shade,

        /// <summary>
        ///     The device is a spa/hot tub, typically contained in and controlled by a pool
        ///     controller device. This device is the representation of the spa itself.
        /// </summary>
        Spa,

        /// <summary>
        ///     The device is primarily a speaker
        /// </summary>
        Speaker,

        /// <summary>
        ///     The device is primarily a vacuum cleaner, typically an automatic robotic vacuum, or
        ///     a pool vacuum
        /// </summary>
        Vacuum,

        /// <summary>
        ///     The device is primarily a vehicle
        /// </summary>
        Vehicle,

        /// <summary>
        ///     The device is primarily a Codec device for video conferencing
        /// </summary>
        VideoConferenceCodec,

        /// <summary>
        ///     The device is primarily a Video Server, like an Apple TV, Roku, etc.
        /// </summary>
        VideoServer
    }
}