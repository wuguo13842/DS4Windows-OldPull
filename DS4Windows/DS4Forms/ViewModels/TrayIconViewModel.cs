﻿/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DS4Windows;
using WPFLocalizeExtension.Extensions;
using DS4WinWPF.Translations;
using System.Linq;
using DS4WinWPF.DS4Forms;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class TrayIconViewModel
    {
        private string tooltipText = "DS4Windows";
        private string iconSource;
        public const string ballonTitle = "DS4Windows";
        public static string trayTitle = $"DS4Windows v{Global.exeversion}";
        private ContextMenu contextMenu;
        private MenuItem changeServiceItem;
        private MenuItem openItem;
        private MenuItem minimizeItem;
        private MenuItem openProgramItem;
        private MenuItem closeItem;
        private int? prevBattery = null;

        // 闪烁相关字段 - 使用设备级 GyroCalibrationBlinker
        private string gyroIcon;
        private string batteryIcon;
        private Dictionary<DS4Device, Action<bool>> _deviceCallbacks = new Dictionary<DS4Device, Action<bool>>();

        public string TooltipText
        {
            get => tooltipText;
            set
            {
                string temp = value;
                if (value.Length > 63) temp = value.Substring(0, 63);
                if (tooltipText == temp) return;
                tooltipText = temp;
                try
                {
                    TooltipTextChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (InvalidOperationException) { }
            }
        }
        public event EventHandler TooltipTextChanged;

        public string IconSource
        {
            get => iconSource;
            set
            {
                if (iconSource == value) return;
                iconSource = value;
                IconSourceChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public ContextMenu ContextMenu { get => contextMenu; }

        public event EventHandler IconSourceChanged;
        public event EventHandler RequestShutdown;
        public event EventHandler RequestOpen;
        public event EventHandler RequestMinimize;
        public event EventHandler RequestServiceChange;

        private ReaderWriterLockSlim _colLocker = new ReaderWriterLockSlim();
        private List<ControllerHolder> controllerList = new List<ControllerHolder>();
        private ProfileList profileListHolder;
        private ControlService controlService;

        public delegate void ProfileSelectedHandler(TrayIconViewModel sender, ControllerHolder item, string profile);
        public event ProfileSelectedHandler ProfileSelected;

        public TrayIconViewModel(ControlService service, ProfileList profileListHolder)
        {
            this.profileListHolder = profileListHolder;
            this.controlService = service;
            contextMenu = new ContextMenu();
            iconSource = Global.iconChoiceResources[Global.UseIconChoice];
            gyroIcon = $"{Global.RESOURCES_PREFIX}/gyro.ico";
            Global.BatteryChanged += UpdateTrayBattery;

            changeServiceItem = new MenuItem()
            {
                Header = GetLocalizedString("ServiceStart"),
                IsEnabled = false
            };
            changeServiceItem.Click += ChangeControlServiceItem_Click;
            openItem = new MenuItem() { Header = GetLocalizedString("MenuOpen"), FontWeight = FontWeights.Bold };
            openItem.Click += OpenMenuItem_Click;
            minimizeItem = new MenuItem() { Header = GetLocalizedString("MenuMinimize") };
            minimizeItem.Click += MinimizeMenuItem_Click;
            openProgramItem = new MenuItem() { Header = GetLocalizedString("MenuOpenProgramFolder") };
            openProgramItem.Click += OpenProgramFolderItem_Click;
            closeItem = new MenuItem() { Header = GetLocalizedString("MenuExit") };
            closeItem.Click += ExitMenuItem_Click;

            PopulateControllerList();
            PopulateToolText();
            PopulateContextMenu();
            SetupEvents();
            profileListHolder.ProfileListCol.CollectionChanged += ProfileListCol_CollectionChanged;

            service.ServiceStarted += BuildControllerList;
            service.ServiceStarted += HookEvents;
            service.ServiceStarted += StartPopulateText;
            service.PreServiceStop += ClearToolText;
            service.PreServiceStop += UnhookEvents;
            service.PreServiceStop += ClearControllerList;
            service.RunningChanged += Service_RunningChanged;
            service.HotplugController += Service_HotplugController;
        }

        private string GetLocalizedString(string key) => LocExtension.GetLocalizedValue<string>(key);

        private void Service_RunningChanged(object sender, EventArgs e)
        {
            string headerKey = controlService.running ? "ServiceStop" : "ServiceStart";
            App.Current.Dispatcher.BeginInvoke((Action)(() =>
            {
                changeServiceItem.Header = GetLocalizedString(headerKey);
                changeServiceItem.IsEnabled = true;
            }));
        }

        private void ClearControllerList(object sender, EventArgs e)
        {
            _colLocker.EnterWriteLock();
            controllerList.Clear();
            _colLocker.ExitWriteLock();
        }

        private void UnhookEvents(object sender, EventArgs e)
        {
            _colLocker.EnterReadLock();
            foreach (ControllerHolder holder in controllerList)
            {
                DS4Device currentDev = holder.Device;
                RemoveDeviceEvents(currentDev);
            }
            _colLocker.ExitReadLock();
        }

        private void Service_HotplugController(ControlService sender, DS4Device device, int index)
        {
            SetupDeviceEvents(device);
            _colLocker.EnterWriteLock();
            controllerList.Add(new ControllerHolder(device, index));
            _colLocker.ExitWriteLock();
        }

        private void ProfileListCol_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            PopulateContextMenu();
        }

        private void BuildControllerList(object sender, EventArgs e) => PopulateControllerList();

        public void PopulateContextMenu()
        {
            contextMenu.Items.Clear();
            ItemCollection items = contextMenu.Items;

            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device currentDev = holder.Device;
                    string macAddress = currentDev.MacAddress;

                    MenuItem controllerItem = new MenuItem() { Header = GetLocalizedString("Controllers") + " " + (holder.Index + 1) };
                    controllerItem.Tag = macAddress;
                    ItemCollection subitems = controllerItem.Items;

                    string currentProfile = Global.ProfilePath[holder.Index];
                    foreach (ProfileEntity entry in profileListHolder.ProfileListCol)
                    {
                        string name = Regex.Replace(entry.Name, "_{1}", "__");
                        MenuItem profileItem = new MenuItem() { Header = name };
                        profileItem.Tag = macAddress;
                        profileItem.Click += ProfileItem_Click;
                        if (entry.Name == currentProfile) profileItem.IsChecked = true;
                        subitems.Add(profileItem);
                    }

                    if (profileListHolder.ProfileListCol.Count > 0) subitems.Add(new Separator());

                    if (currentDev.CanDisconnect)
                    {
                        MenuItem disconnectItem = new MenuItem() { Header = GetLocalizedString("Disconnect") };
                        disconnectItem.Click += DisconnectMenuItem_Click;
                        disconnectItem.Tag = macAddress;
                        subitems.Add(disconnectItem);
                    }

                    if (currentDev?.SixAxis != null)
                    {
                        MenuItem gyroItem = new MenuItem() { Header = GetLocalizedString("GyroCalibration") };
                        gyroItem.Click += CalibrateGyroMenuItem_Click;
                        gyroItem.Tag = macAddress;
                        subitems.Add(gyroItem);
                    }

                    items.Add(controllerItem);
                }

                if (controllerList.Count > 0) items.Add(new Separator());
                PopulateStaticItems();
            }
        }

        private void ChangeControlServiceItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            changeServiceItem.IsEnabled = false;
            RequestServiceChange?.Invoke(this, EventArgs.Empty);
        }

        private void OpenProgramFolderItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(Global.exedirpath);
            startInfo.UseShellExecute = true;
            using (Process temp = Process.Start(startInfo)) { }
        }

        private void OpenMenuItem_Click(object sender, System.Windows.RoutedEventArgs e) => RequestOpen?.Invoke(this, EventArgs.Empty);
        private void MinimizeMenuItem_Click(object sender, System.Windows.RoutedEventArgs e) => RequestMinimize?.Invoke(this, EventArgs.Empty);

        private void ProfileItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            string macAddress = item.Tag as string;
            if (string.IsNullOrEmpty(macAddress)) return;

            ControllerHolder holder = null;
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                holder = controllerList.FirstOrDefault(h => h.Device.MacAddress == macAddress);
            }
            if (holder == null) return;

            string tempProfileName = Regex.Replace(item.Header.ToString(), "_{2}", "_");
            ProfileSelected?.Invoke(this, holder, tempProfileName);
        }

        private void DisconnectMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            string macAddress = item.Tag as string;
            if (string.IsNullOrEmpty(macAddress)) return;

            ControllerHolder holder = null;
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                holder = controllerList.FirstOrDefault(h => h.Device.MacAddress == macAddress);
            }
            if (holder == null) return;

            DS4Device tempDev = holder.Device;
            if (tempDev != null && tempDev.CanDisconnect)
            {
                if (tempDev.ConnectionType == ConnectionType.BT) tempDev.DisconnectBT();
                else if (tempDev.ConnectionType == ConnectionType.SONYWA) tempDev.DisconnectDongle();
            }
        }

        private void CalibrateGyroMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            string macAddress = item.Tag as string;
            if (string.IsNullOrEmpty(macAddress)) return;

            ControllerHolder holder = null;
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                holder = controllerList.FirstOrDefault(h => h.Device.MacAddress == macAddress);
            }
            if (holder == null)
            {
                PopulateContextMenu();
                Debug.WriteLine($"校准失败：未找到设备 {macAddress}");
                return;
            }

            DS4Device device = holder.Device;
            if (device != null)
            {
                string message = string.Format(Strings.GyroCalibrationStarted, holder.Index + 1);
                AppLogger.LogToTray(message, false);
                device.CalibrationBlinker?.ForceResetCalibration();
            }
        }

        private void PopulateControllerList()
        {
            int idx = 0;
            _colLocker.EnterWriteLock();
            foreach (DS4Device currentDev in controlService.slotManager.ControllerColl)
            {
                controllerList.Add(new ControllerHolder(currentDev, idx));
                idx++;
            }
            _colLocker.ExitWriteLock();
        }

        private void StartPopulateText(object sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => PopulateToolText());

        private void PopulateToolText()
        {
            List<string> items = new List<string> { trayTitle };
            int idx = 1;
            _colLocker.EnterReadLock();
            foreach (ControllerHolder holder in controllerList)
            {
                DS4Device currentDev = holder.Device;
                items.Add($"{idx}: {currentDev.ConnectionType} {currentDev.Battery}%{(currentDev.Charging ? "+" : "")}");
                idx++;
            }
            _colLocker.ExitReadLock();
            TooltipText = string.Join("\n", items);
        }

        private void SetupEvents()
        {
            _colLocker.EnterReadLock();
            foreach (ControllerHolder holder in controllerList)
            {
                DS4Device currentDev = holder.Device;
                SetupDeviceEvents(currentDev);
            }
            _colLocker.ExitReadLock();
        }

        private void SetupDeviceEvents(DS4Device device)
        {
            device.BatteryChanged += UpdateForBattery;
            device.ChargingChanged += UpdateForBattery;
            device.Removal += CurrentDev_Removal;

            // 订阅校准开始事件用于发送系统通知
            device.SixAxis.CalibrationStarted += (s, e) =>
            {
                int idx = GetDeviceIndex(device);
                if (idx >= 0) AppLogger.LogGyroCalibrationStarted(idx);
            };

            // 订阅校准停止事件，恢复托盘图标
            device.SixAxis.CalibrationStopped += (s, e) =>
            {
                RestoreTrayIcon();
            };

            // 注册闪烁回调
            var blinker = device.CalibrationBlinker;
            if (blinker != null)
            {
                Action<bool> callback = (visible) =>
                {
                    if (visible) IconSource = gyroIcon;
                    else IconSource = null;
                };
                blinker.RegisterCallback(callback);
                lock (_deviceCallbacks) { _deviceCallbacks[device] = callback; }
            }
        }

        private void RemoveDeviceEvents(DS4Device device)
        {
            device.BatteryChanged -= UpdateForBattery;
            device.ChargingChanged -= UpdateForBattery;
            device.Removal -= CurrentDev_Removal;

            var blinker = device.CalibrationBlinker;
            if (blinker != null)
            {
                lock (_deviceCallbacks)
                {
                    if (_deviceCallbacks.TryGetValue(device, out var callback))
                    {
                        blinker.UnregisterCallback(callback);
                        _deviceCallbacks.Remove(device);
                    }
                }
            }
        }

        private int GetDeviceIndex(DS4Device device)
        {
            if (device == null) return -1;
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                for (int i = 0; i < controllerList.Count; i++)
                {
                    if (controllerList[i].Device == device) return i;
                }
            }
            return -1;
        }

        private void CurrentDev_Removal(object sender, EventArgs e)
        {
            DS4Device currentDev = sender as DS4Device;
            ControllerHolder item = null;
            int idx = 0;

            if (currentDev != null)
            {
                var blinker = currentDev.CalibrationBlinker;
                if (blinker != null)
                {
                    lock (_deviceCallbacks)
                    {
                        if (_deviceCallbacks.TryGetValue(currentDev, out var callback))
                        {
                            blinker.UnregisterCallback(callback);
                            _deviceCallbacks.Remove(currentDev);
                        }
                    }
                }
            }

            using (WriteLocker locker = new WriteLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    if (currentDev == holder.Device) { item = holder; break; }
                    idx++;
                }
                if (item != null)
                {
                    controllerList.RemoveAt(idx);
                    RemoveDeviceEvents(currentDev);
                }
            }

            PopulateToolText();
            Application.Current.Dispatcher.Invoke(() => PopulateContextMenu());
        }

        private void HookEvents(object sender, EventArgs e) => SetupEvents();

        private void UpdateForBattery(object sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => PopulateToolText());

        private void ClearToolText(object sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => TooltipText = "DS4Windows");

        private void PopulateStaticItems()
        {
            ItemCollection items = contextMenu.Items;
            items.Add(changeServiceItem);
            items.Add(openItem);
            items.Add(minimizeItem);
            items.Add(new Separator());
            items.Add(closeItem);
        }

        public void ClearContextMenu()
        {
            contextMenu.Items.Clear();
            PopulateContextMenu();
            foreach (var kvp in _deviceCallbacks)
            {
                kvp.Key.CalibrationBlinker?.UnregisterCallback(kvp.Value);
            }
            _deviceCallbacks.Clear();
        }

        private void RestoreTrayIcon()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Global.UseIconChoice == TrayIconChoice.Battery)
                {
                    IconSource = batteryIcon ?? Global.iconChoiceResources[TrayIconChoice.Default];
                }
                else
                {
                    IconSource = Global.iconChoiceResources[Global.UseIconChoice];
                }
            }));
        }

        private void UpdateTrayBattery(object sender, byte percentage)
        {
            string newIcon = percentage switch
            {
                < 10 => $"{Global.RESOURCES_PREFIX}/0.ico",
                >= 10 and < 20 => $"{Global.RESOURCES_PREFIX}/10.ico",
                >= 20 and < 30 => $"{Global.RESOURCES_PREFIX}/20.ico",
                >= 30 and < 40 => $"{Global.RESOURCES_PREFIX}/30.ico",
                >= 40 and < 50 => $"{Global.RESOURCES_PREFIX}/40.ico",
                >= 50 and < 60 => $"{Global.RESOURCES_PREFIX}/50.ico",
                >= 60 and < 70 => $"{Global.RESOURCES_PREFIX}/60.ico",
                >= 70 and < 80 => $"{Global.RESOURCES_PREFIX}/70.ico",
                >= 80 and < 90 => $"{Global.RESOURCES_PREFIX}/80.ico",
                >= 90 and < 100 => $"{Global.RESOURCES_PREFIX}/90.ico",
                100 => $"{Global.RESOURCES_PREFIX}/100.ico",
                _ => $"{Global.RESOURCES_PREFIX}/DS4W.ico"
            };
            batteryIcon = newIcon;
            RestoreTrayIcon();
        }

        private void ExitMenuItem_Click(object sender, System.Windows.RoutedEventArgs e) => RequestShutdown?.Invoke(this, EventArgs.Empty);
    }

    public class ControllerHolder
    {
        private DS4Device device;
        private int index;
        public DS4Device Device { get => device; }
        public int Index { get => index; }
        public ControllerHolder(DS4Device device, int index)
        {
            this.device = device;
            this.index = index;
        }
    }
}