﻿using HidLibrary;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using Tools;

namespace GHelper
{
    public class KeyboardListener
    {

        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        public KeyboardListener(Action<int> KeyHandler)
        {
            HidDevice? input = AsusUSB.GetDevice();
            if (input == null) return;

            Logger.WriteLine($"Input: {input.DevicePath}");

            var task = Task.Run(() =>
            {
                try
                {
                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        var data = input.Read().Data;
                        if (data.Length > 1 && data[0] == AsusUSB.INPUT_HID_ID && data[1] > 0)
                        {
                            Logger.WriteLine($"Key: {data[1]}");
                            KeyHandler(data[1]);
                        }
                    }
                    Logger.WriteLine("Listener stopped");

                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.ToString());
                }
            });


        }

        public void Dispose()
        {
            cancellationTokenSource?.Cancel();
        }
    }


    public class InputDispatcher
    {
        System.Timers.Timer timer = new System.Timers.Timer(1000);
        public bool backlight = true;

        private static nint windowHandle;

        public static Keys keyProfile = Keys.F5;

        KeyboardListener listener;

        KeyHandler m1, m2, togggle;

        public InputDispatcher(nint handle)
        {

            windowHandle = handle;

            byte[] result = Program.acpi.DeviceInit();
            Debug.WriteLine($"Init: {BitConverter.ToString(result)}");

            Program.acpi.SubscribeToEvents(WatcherEventArrived);
            //Task.Run(Program.acpi.RunListener);


            // CTRL + SHIFT + F5 to cycle profiles
            if (AppConfig.getConfig("keybind_profile") != -1) keyProfile = (Keys)AppConfig.getConfig("keybind_profile");

            togggle = new KeyHandler(KeyHandler.SHIFT | KeyHandler.CTRL, keyProfile, windowHandle);
            m1 = new KeyHandler(KeyHandler.NOMOD, Keys.VolumeDown, windowHandle);
            m2 = new KeyHandler(KeyHandler.NOMOD, Keys.VolumeUp, windowHandle);

            RegisterKeys();

            timer.Elapsed += Timer_Elapsed;

        }

        private void Timer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            TimeSpan iddle = NativeMethods.GetIdleTime();
            int kb_timeout = AppConfig.getConfig("keyboard_timeout", 60);
            
            if (kb_timeout == 0) return;

            if (backlight && iddle.TotalSeconds > kb_timeout)
            {
                backlight = false;
                AsusUSB.ApplyBrightness(0);
            }

            if (!backlight && iddle.TotalSeconds < kb_timeout)
            {
                backlight = true;
                AsusUSB.ApplyBrightness(AppConfig.getConfig("keyboard_brightness"));
            }

            //Debug.WriteLine(iddle.TotalSeconds);
        }

        public void Init()
        {
            if (listener is not null) listener.Dispose();

            Program.acpi.DeviceInit();

            if (!OptimizationService.IsRunning())
                listener = new KeyboardListener(HandleEvent);

            timer.Enabled = (AppConfig.getConfig("keyboard_timeout") > 0 && SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online);

        }



        public void RegisterKeys()
        {

            string actionM1 = AppConfig.getConfigString("m1");
            string actionM2 = AppConfig.getConfigString("m2");

            togggle.Unregiser();
            m1.Unregiser();
            m2.Unregiser();

            if (keyProfile != Keys.None)
            {
                togggle.Register();
            }

            if (actionM1 is not null && actionM1.Length > 0)
            {
                m1.Register();
            }

            if (actionM2 is not null && actionM2.Length > 0)
            {
                m2.Register();
            }

        }

        static void CustomKey(string configKey = "m3")
        {
            string command = AppConfig.getConfigString(configKey + "_custom");
            int intKey;

            try
            {
                intKey = Convert.ToInt32(command, 16);
            }
            catch
            {
                intKey = -1;
            }


            if (intKey > 0)
                NativeMethods.KeyPress(intKey);
            else
                LaunchProcess(command);

        }

        public static void KeyProcess(string name = "m3")
        {
            string action = AppConfig.getConfigString(name);

            if (action is null || action.Length <= 1)
            {
                if (name == "m4")
                    action = "ghelper";
                if (name == "fnf4")
                    action = "aura";
                if (name == "fnf5")
                    action = "performance";
                if (name == "m3" && !OptimizationService.IsRunning())
                    action = "micmute";
            }

            switch (action)
            {
                case "mute":
                    NativeMethods.KeyPress(NativeMethods.VK_VOLUME_MUTE);
                    break;
                case "play":
                    NativeMethods.KeyPress(NativeMethods.VK_MEDIA_PLAY_PAUSE);
                    break;
                case "screenshot":
                    NativeMethods.KeyPress(NativeMethods.VK_SNAPSHOT);
                    break;
                case "screen":
                    NativeMethods.TurnOffScreen(Program.settingsForm.Handle);
                    break;
                case "miniled":
                    Program.settingsForm.BeginInvoke(Program.settingsForm.ToogleMiniled);
                    break;
                case "aura":
                    Program.settingsForm.BeginInvoke(Program.settingsForm.CycleAuraMode);
                    break;
                case "performance":
                    Program.settingsForm.BeginInvoke(Program.settingsForm.CyclePerformanceMode);
                    break;
                case "ghelper":
                    Program.settingsForm.BeginInvoke(delegate
                    {
                        Program.SettingsToggle();
                    });
                    break;
                case "custom":
                    CustomKey(name);
                    break;
                case "micmute":
                    using (var enumerator = new MMDeviceEnumerator())
                    {
                        var commDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                        bool muteStatus = !commDevice.AudioEndpointVolume.Mute;
                        commDevice.AudioEndpointVolume.Mute = muteStatus;
                        Program.settingsForm.BeginInvoke(Program.settingsForm.RunToast, muteStatus ? "Muted" : "Unmuted", muteStatus ? ToastIcon.MicrophoneMute : ToastIcon.Microphone);
                    }
                    break;

                default:
                    break;
            }
        }

        static bool GetTouchpadState()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status", false))
            {
                return (key?.GetValue("Enabled")?.ToString() == "1");
            }
        }

        static void TabletMode()
        {
            bool touchpadState = GetTouchpadState();
            bool tabletState = Program.acpi.DeviceGet(AsusACPI.TabletState) > 0;

            Logger.WriteLine("Tablet: " + tabletState + " Touchpad: " + touchpadState);

            if ((tabletState && touchpadState) || (!tabletState && !touchpadState)) AsusUSB.TouchpadToggle();

        }

        static void HandleEvent(int EventID)
        {
            switch (EventID)
            {
                case 124:    // M3
                    KeyProcess("m3");
                    return;
                case 56:    // M4 / Rog button
                    KeyProcess("m4");
                    return;
                case 174:   // FN+F5
                    Program.settingsForm.BeginInvoke(Program.settingsForm.CyclePerformanceMode);
                    return;
                case 179:   // FN+F4
                case 178:   // FN+F4
                    KeyProcess("fnf4");
                    return;
                case 189: // Tablet mode 
                    TabletMode();
                    return;
            }

            if (OptimizationService.IsRunning()) return;

            // Asus Optimization service Events 

            int backlight = AppConfig.getConfig("keyboard_brightness");
            string[] backlightNames = new string[] { "Off", "Low", "Mid", "Max" };

            switch (EventID)
            {
                case 197: // FN+F2
                    backlight = Math.Max(0, backlight - 1);
                    AppConfig.setConfig("keyboard_brightness", backlight);
                    AsusUSB.ApplyBrightness(backlight);
                    Program.settingsForm.BeginInvoke(Program.settingsForm.RunToast, backlightNames[backlight], ToastIcon.BacklightDown);
                    break;
                case 196: // FN+F3
                    backlight = Math.Min(3, backlight + 1);
                    AppConfig.setConfig("keyboard_brightness", backlight);
                    AsusUSB.ApplyBrightness(backlight);
                    Program.settingsForm.BeginInvoke(Program.settingsForm.RunToast, backlightNames[backlight], ToastIcon.BacklightUp);
                    break;
                case 199: // ON Z13 - FN+F11 - cycles backlight
                    if (++backlight > 3) backlight = 0;
                    AppConfig.setConfig("keyboard_brightness", backlight);
                    AsusUSB.ApplyBrightness(backlight);
                    Program.settingsForm.BeginInvoke(Program.settingsForm.RunToast, backlightNames[backlight], ToastIcon.BacklightUp);
                    break;
                case 16: // FN+F7
                    Program.acpi.DeviceSet(AsusACPI.UniversalControl, 0x10, "Brightness");
                    break;
                case 32: // FN+F8
                    Program.acpi.DeviceSet(AsusACPI.UniversalControl, 0x20, "Brightness");
                    break;
                case 107: // FN+F10
                    bool touchpadState = GetTouchpadState();
                    AsusUSB.TouchpadToggle();
                    Program.settingsForm.BeginInvoke(Program.settingsForm.RunToast, touchpadState ? "Off" : "On", ToastIcon.Touchpad);
                    break;
                case 108: // FN+F11
                    Program.acpi.DeviceSet(AsusACPI.UniversalControl, 0x6c, "Sleep");
                    //NativeMethods.SetSuspendState(false, true, true);
                    break;
            }
        }


        static void LaunchProcess(string command = "")
        {

            try
            {
                string executable = command.Split(' ')[0];
                string arguments = command.Substring(executable.Length).Trim();
                Process proc = Process.Start(executable, arguments);
            }
            catch
            {
                Logger.WriteLine("Failed to run  " + command);
            }


        }



        static void WatcherEventArrived(object sender, EventArrivedEventArgs e)
        {
            if (e.NewEvent is null) return;
            int EventID = int.Parse(e.NewEvent["EventID"].ToString());
            Logger.WriteLine("WMI event " + EventID);
            HandleEvent(EventID);
        }
    }
}
