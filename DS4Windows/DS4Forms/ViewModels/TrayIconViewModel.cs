/*
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
using System.Windows.Threading;

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

        // 陀螺校准闪烁相关
        private DispatcherTimer gyroCalibrationBlinkTimer;
        private bool blinkState;
        private string normalIconSource; // 保存用户配置的正常图标

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

        public delegate void ProfileSelectedHandler(TrayIconViewModel sender,
            ControllerHolder item, string profile);
        public event ProfileSelectedHandler ProfileSelected;

        //public TrayIconViewModel(Tester tester)
        public TrayIconViewModel(ControlService service, ProfileList profileListHolder)
        {
            this.profileListHolder = profileListHolder;
            this.controlService = service;
            contextMenu = new ContextMenu();
            normalIconSource = Global.iconChoiceResources[Global.UseIconChoice];
            iconSource = normalIconSource;
            Global.BatteryChanged += UpdateTrayBattery; // 仅当用户选择电池图标时有用

            // 初始化菜单项
            changeServiceItem = new MenuItem()
            {
                Header = GetLocalizedString("ServiceStart"),
                IsEnabled = false
            };
            changeServiceItem.Click += ChangeControlServiceItem_Click;
            openItem = new MenuItem() { Header = GetLocalizedString("MenuOpen"),
                FontWeight = FontWeights.Bold };
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
        	/*tester.StartControllers += HookBatteryUpdate;
            tester.StartControllers += StartPopulateText;
            tester.PreRemoveControllers += ClearToolText;
            tester.HotplugControllers += HookBatteryUpdate;
            tester.HotplugControllers += StartPopulateText;
			*/

            // 初始化陀螺校准闪烁定时器（500ms 间隔）
            gyroCalibrationBlinkTimer = new DispatcherTimer();
            gyroCalibrationBlinkTimer.Interval = TimeSpan.FromMilliseconds(500);
            gyroCalibrationBlinkTimer.Tick += GyroCalibrationBlinkTimer_Tick;
            gyroCalibrationBlinkTimer.Start();
        }

        private string GetLocalizedString(string key)
        {
            return LocExtension.GetLocalizedValue<string>(key);
        }

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

        private void ProfileListCol_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            PopulateContextMenu();
        }

        private void BuildControllerList(object sender, EventArgs e)
        {
            PopulateControllerList();
        }

        public void PopulateContextMenu()
        {
            contextMenu.Items.Clear();
            ItemCollection items = contextMenu.Items;
            MenuItem item;
            int idx = 0;

            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device currentDev = holder.Device;
                    item = new MenuItem() { Header = $"Controller {idx + 1}" };
                    item.Tag = idx;
                    //item.ContextMenu = new ContextMenu();
                    ItemCollection subitems = item.Items;
                    string currentProfile = Global.ProfilePath[idx];
                    foreach (ProfileEntity entry in profileListHolder.ProfileListCol)
                    {
                        // Need to escape profile name to disable Access Keys for control
                        string name = entry.Name;
                        name = Regex.Replace(name, "_{1}", "__");
                        MenuItem temp = new MenuItem() { Header = name };
                        temp.Tag = idx;
                        temp.Click += ProfileItem_Click;
                        if (entry.Name == currentProfile)
                        {
                            temp.IsChecked = true;
                        }

                        subitems.Add(temp);
                    }

                    items.Add(item);
                    idx++;
                }

                item = new MenuItem() {  Header = GetLocalizedString("MenuDisconnect") };
                idx = 0;
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device tempDev = holder.Device;
                    if (tempDev.Synced && !tempDev.Charging)
                    {
                        MenuItem subitem = new MenuItem() { Header = $"Disconnect Controller {idx + 1}" };
                        subitem.Click += DisconnectMenuItem_Click;
                        subitem.Tag = idx;
                        item.Items.Add(subitem);
                    }

                    idx++;
                }

                if (idx == 0)
                {
                    item.IsEnabled = false;
                }
            }

            items.Add(item);
            items.Add(new Separator());
            PopulateStaticItems();
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
            using (Process temp = Process.Start(startInfo))
            {
            }
        }

        private void OpenMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestOpen?.Invoke(this, EventArgs.Empty);
        }

        private void MinimizeMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestMinimize?.Invoke(this, EventArgs.Empty);
        }

        private void ProfileItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            int idx = Convert.ToInt32(item.Tag);
            ControllerHolder holder = controllerList[idx];
            // Un-escape underscores is MenuItem header. Header holds the profile name
            string tempProfileName = Regex.Replace(item.Header.ToString(),
                "_{2}", "_");
            ProfileSelected?.Invoke(this, holder, tempProfileName);
        }

        private void DisconnectMenuItem_Click(object sender,
            System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            int idx = Convert.ToInt32(item.Tag);
            ControllerHolder holder = controllerList[idx];
            DS4Device tempDev = holder?.Device;
            if (tempDev != null && tempDev.Synced && !tempDev.Charging)
            {
                if (tempDev.ConnectionType == ConnectionType.BT)
                {
                    //tempDev.StopUpdate();
                    tempDev.DisconnectBT();
                }
                else if (tempDev.ConnectionType == ConnectionType.SONYWA)
                {
                    tempDev.DisconnectDongle();
                }
            }

            //controllerList[idx] = null;
        }

        private void PopulateControllerList()
        {
            //IEnumerable<DS4Device> devices = DS4Devices.getDS4Controllers();
            int idx = 0;
            _colLocker.EnterWriteLock();
            foreach (DS4Device currentDev in controlService.slotManager.ControllerColl)
            {
                controllerList.Add(new ControllerHolder(currentDev, idx));
                idx++;
            }
            _colLocker.ExitWriteLock();
        }

        private void StartPopulateText(object sender, EventArgs e)
        {
            if (!gyroCalibrationBlinkTimer.IsEnabled)
            {
                gyroCalibrationBlinkTimer.Start();
            }
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PopulateToolText();
            });
            //PopulateContextMenu();
        }

        private void PopulateToolText()
        {
            List<string> items = new List<string>();
            items.Add(trayTitle);
            //IEnumerable<DS4Device> devices = DS4Devices.getDS4Controllers();
            int idx = 1;
            //foreach (DS4Device currentDev in devices)
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
            //IEnumerable<DS4Device> devices = DS4Devices.getDS4Controllers();
            //foreach (DS4Device currentDev in devices)
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
        }

        private void RemoveDeviceEvents(DS4Device device)
        {
            device.BatteryChanged -= UpdateForBattery;
            device.ChargingChanged -= UpdateForBattery;
            device.Removal -= CurrentDev_Removal;
        }

        private void CurrentDev_Removal(object sender, EventArgs e)
        {
            DS4Device currentDev = sender as DS4Device;
            ControllerHolder item = null;
            int idx = 0;

            using (WriteLocker locker = new WriteLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    if (currentDev == holder.Device)
                    {
                        item = holder;
                        break;
                    }

                    idx++;
                }

                if (item != null)
                {
                    controllerList.RemoveAt(idx);
                    RemoveDeviceEvents(currentDev);
                }
            }

            PopulateToolText();
        }

        private void HookEvents(object sender, EventArgs e)
        {
            SetupEvents();
        }

        private void UpdateForBattery(object sender, EventArgs e)
        {
            // Force invoke from GUI thread
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PopulateToolText();
            });
        }

        private void ClearToolText(object sender, EventArgs e)
        {
            gyroCalibrationBlinkTimer?.Stop();
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                TooltipText = "DS4Windows";
            });
            //contextMenu.Items.Clear();
        }

        private void PopulateStaticItems()
        {
            ItemCollection items = contextMenu.Items;
            items.Add(changeServiceItem);
            items.Add(openItem);
            items.Add(minimizeItem);
            items.Add(openProgramItem);
            items.Add(new Separator());
            items.Add(closeItem);
        }

        public void ClearContextMenu()
        {
            contextMenu.Items.Clear();
            PopulateStaticItems();
        }

        private void UpdateTrayBattery(object sender, byte percentage)
        {
            // 此方法只在用户选择了“电池”图标时被调用，但我们的闪烁逻辑可能覆盖它。
            // 为了兼容，在非闪烁状态且用户选择了电池图标时，才调用此方法更新图标。
            // 但为了简化，我们在闪烁时直接覆盖图标，闪烁结束后恢复 normalIconSource（配置图标）。
            // 如果用户选择了电池图标，normalIconSource 是 battery 图标的路径，但 battery 图标本身会随电量变化，
            // 所以我们需要在非闪烁时确保电池图标能更新。为此，我们在 UpdateTrayBattery 中也更新 normalIconSource，
            // 并立即应用（如果当前不在闪烁状态）。
            normalIconSource = percentage switch
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

            // 如果当前不在闪烁状态，则立即更新图标
            if (!blinkState)
            {
                IconSource = normalIconSource;
            }
        }

        private void ExitMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestShutdown?.Invoke(this, EventArgs.Empty);
        }

        // 陀螺校准闪烁定时器逻辑
        private void GyroCalibrationBlinkTimer_Tick(object sender, EventArgs e)
        {
            // 检查是否有任一设备正在校准（CntCalibrating > 0）
            bool anyCalibrating = false;
            _colLocker.EnterReadLock();
            try
            {
                foreach (var holder in controllerList)
                {
                    var device = holder.Device;
                    if (device != null && device.SixAxis.CntCalibrating > 0)
                    {
                        anyCalibrating = true;
                        break;
                    }
                }
            }
            finally
            {
                _colLocker.ExitReadLock();
            }

            if (!anyCalibrating)
            {
                // 没有设备校准：恢复常规图标
                if (blinkState)
                {
                    blinkState = false;
                    IconSource = normalIconSource; // 恢复用户配置的图标
                }
                return;
            }

            // 有设备校准：切换闪烁状态
            blinkState = !blinkState;

            if (blinkState)
            {
                // 闪烁时显示一个特殊图标（可使用 DS4W.ico 或专用图标）
                IconSource = $"{Global.RESOURCES_PREFIX}/gyro.ico";
            }
            else
            {
                // 非闪烁状态显示常规图标（用户配置的图标）
                IconSource = normalIconSource;
            }
        }
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