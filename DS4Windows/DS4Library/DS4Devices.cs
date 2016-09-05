using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DS4Windows
{
    public class DS4Devices
    {
        private static Dictionary<string, DS4Device> Devices = new Dictionary<string, DS4Device>();
        private static HashSet<String> DevicePaths = new HashSet<String>();
        public static bool isExclusiveMode = false;

        private static string devicePathToInstanceId(string devicePath)
        {
            string deviceInstanceId = devicePath;
            deviceInstanceId = deviceInstanceId.Remove(0, deviceInstanceId.LastIndexOf('\\') + 1);
            deviceInstanceId = deviceInstanceId.Remove(deviceInstanceId.LastIndexOf('{'));
            deviceInstanceId = deviceInstanceId.Replace('#', '\\');
            if (deviceInstanceId.EndsWith("\\"))
            {
                deviceInstanceId = deviceInstanceId.Remove(deviceInstanceId.Length - 1);
            }
            return deviceInstanceId;
        }

        //enumerates ds4 controllers in the system
        public static void findControllers()
        {
            lock (Devices)
            {
                int[] pid = { 0x5C4 };
                IEnumerable<HidDevice> hDevices = HidDevices.Enumerate(0x054C, pid);
                // Sort Bluetooth first in case USB is also connected on the same controller.
                hDevices = hDevices.OrderBy<HidDevice, ConnectionType>((HidDevice d) => { return DS4Device.HidConnectionType(d); });

                foreach (HidDevice hDevice in hDevices)
                {
                    if (DevicePaths.Contains(hDevice.DevicePath))
                        continue; // BT/USB endpoint already open once
                    if (!hDevice.IsOpen)
                    {
                        hDevice.OpenDevice(isExclusiveMode);
                        if (!hDevice.IsOpen && isExclusiveMode)
                        {
                            try
                            {
                                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                                WindowsPrincipal principal = new WindowsPrincipal(identity);
                                bool elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);

                                if (!elevated)
                                {
                                    // Launches an elevated child process to re-enable device
                                    string exeName = Process.GetCurrentProcess().MainModule.FileName;
                                    ProcessStartInfo startInfo = new ProcessStartInfo(exeName);
                                    startInfo.Verb = "runas";
                                    startInfo.Arguments = "re-enabledevice " + devicePathToInstanceId(hDevice.DevicePath);
                                    Process child = Process.Start(startInfo);
                                    if (!child.WaitForExit(5000))
                                    {
                                        child.Kill();
                                    }
                                    else if (child.ExitCode == 0)
                                    {
                                        hDevice.OpenDevice(isExclusiveMode);
                                    }
                                }
                                else
                                {
                                    Program.reEnableDevice(devicePathToInstanceId(hDevice.DevicePath));
                                    hDevice.OpenDevice(isExclusiveMode);
                                }
                            }
                            catch (Exception) { }
                        }
                        
                        // TODO in exclusive mode, try to hold both open when both are connected
                        if (isExclusiveMode && !hDevice.IsOpen)
                            hDevice.OpenDevice(false);
                    }
                    if (hDevice.IsOpen)
                    {
                        if (Devices.ContainsKey(hDevice.readSerial()))
                            continue; // happens when the BT endpoint already is open and the USB is plugged into the same host
                        else
                        {
                            DS4Device ds4Device = new DS4Device(hDevice);
                            ds4Device.Removal += On_Removal;
                            Devices.Add(ds4Device.MacAddress, ds4Device);
                            DevicePaths.Add(hDevice.DevicePath);
                            ds4Device.StartUpdate();
                        }
                    }
                }
                
            }
        }

        //allows to get DS4Device by specifying unique MAC address
        //format for MAC address is XX:XX:XX:XX:XX:XX
        public static DS4Device getDS4Controller(string mac)
        {
            lock (Devices)
            {
                DS4Device device = null;
                try
                {
                    Devices.TryGetValue(mac, out device);
                }
                catch (ArgumentNullException) { }
                return device;
            }
        }
        
        //returns DS4 controllers that were found and are running
        public static IEnumerable<DS4Device> getDS4Controllers()
        {
            lock (Devices)
            {
                DS4Device[] controllers = new DS4Device[Devices.Count];
                Devices.Values.CopyTo(controllers, 0);
                return controllers;
            }
        }

        public static void stopControllers()
        {
            lock (Devices)
            {
                IEnumerable<DS4Device> devices = getDS4Controllers();
                foreach (DS4Device device in devices)
                {
                    device.StopUpdate();
                    device.HidDevice.CloseDevice();
                }
                Devices.Clear();
                DevicePaths.Clear();
            }
        }

        //called when devices is diconnected, timed out or has input reading failure
        public static void On_Removal(object sender, EventArgs e)
        {
            lock (Devices)
            {
                DS4Device device = (DS4Device)sender;
                device.HidDevice.CloseDevice();
                Devices.Remove(device.MacAddress);
                DevicePaths.Remove(device.HidDevice.DevicePath);
            }
        }
    }
}
