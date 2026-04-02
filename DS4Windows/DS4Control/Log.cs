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
using DS4WinWPF.Translations;
using DS4Windows.InputDevices;

namespace DS4Windows
{
    public class AppLogger
    {
        public static event EventHandler<DebugEventArgs> TrayIconLog;
        public static event EventHandler<DebugEventArgs> GuiLog;
        public static void LogToGui(string data, bool warning, bool temporary = false)
        {
            if (GuiLog != null)
            {
                GuiLog(null, new DebugEventArgs(data, warning, temporary));
            }
        }

        public static void LogToTray(string data, bool warning = false, bool ignoreSettings = false)
        {
            if (TrayIconLog != null)
            {
                if (ignoreSettings)
                    TrayIconLog(ignoreSettings, new DebugEventArgs(data, warning));
                else
                    TrayIconLog(null, new DebugEventArgs(data, warning));
            }
        }
		
        /// <summary>
        /// 记录陀螺仪校准开始通知
        /// </summary>
        /// <param name="deviceIndex">设备索引（0-based）</param>
		
        // 全局冷却：上次发送陀螺仪通知的时间
        private static DateTime lastGyroNotificationTime = DateTime.MinValue;
        private static readonly object notificationLock = new object();
        private const int GYRO_NOTIFICATION_COOLDOWN_SECONDS = 10;

        // public static void LogGyroCalibrationStarted(int deviceIndex)
        // {
            // string message = string.Format(Strings.GyroCalibrationStarted, deviceIndex + 1);
            // LogToTray(message, false, true);
        // }
        public static void LogGyroCalibrationStarted(int? deviceIndex = null)
        {
            lock (notificationLock)
            {
                DateTime now = DateTime.Now;
                if ((now - lastGyroNotificationTime).TotalSeconds < GYRO_NOTIFICATION_COOLDOWN_SECONDS)
                {
                    return; // 仍在冷却期内，不发送
                }

                lastGyroNotificationTime = now;
                string message = Strings.GyroCalibrationStarted;
                LogToTray(message, false, true);
            }
        }
		
    }
}

