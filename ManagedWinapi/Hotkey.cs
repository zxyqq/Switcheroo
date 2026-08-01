/*
 * ManagedWinapi - A collection of .NET components that wrap PInvoke calls to 
 * access native API by managed code. http://mwinapi.sourceforge.net/
 * Copyright (C) 2006 Michael Schierl
 * 
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; see the file COPYING. if not, visit
 * http://www.gnu.org/licenses/lgpl.html or write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ManagedWinapi.Hooks;

namespace ManagedWinapi
{

    /// <summary>
    /// Specifies a component that creates a global keyboard hotkey.
    /// </summary>
    [DefaultEvent("HotkeyPressed")]
    public class Hotkey : Component
    {

        /// <summary>
        /// Occurs when the hotkey is pressed.
        /// </summary>
        public event EventHandler HotkeyPressed;

        // Active hotkey instances in this process, used to detect two Switcheroo
        // hotkeys colliding on the same combo (mirrors the old RegisterHotKey
        // ERROR_HOTKEY_ALREADY_REGISTERED behaviour for the practical case).
        private static readonly object _staticLock = new object();
        private static readonly HashSet<Hotkey> _activeInstances = new HashSet<Hotkey>();

        private bool isDisposed = false, isEnabled = false;
        private Keys _keyCode;
        private bool _ctrl, _alt, _shift, _windows;
        private string _name;

        // A global hotkey is implemented with a low-level keyboard hook instead of
        // RegisterHotKey. RegisterHotKey leaks the Alt key-up event to the foreground
        // window, so pressing Alt+<key> makes menu-bearing apps (Chrome, Explorer,
        // Office) activate their menu bar. The low-level hook swallows the trigger
        // key, and intercepts the Alt key-up: it blocks the real WM_SYSKEYUP(VK_MENU)
        // (which is what enters menu mode) and delivers a non-system WM_KEYUP(VK_MENU)
        // instead, so the key state is released without focusing the menu. Blocking the
        // key-up outright would leave the app's keyboard state stuck ("Alt never
        // released"), hence the substituted WM_KEYUP.
        private LowLevelKeyboardHook _hook;
        private bool _mainKeyIsDown;   // edge detect so auto-repeat does not refire
        private bool _interceptAltUp;  // block the next Alt key-up and repair state
        private IntPtr _altDownHWnd;   // window that had focus when Alt went down

        /// <summary>
        /// Initializes a new instance of this class with the specified container.
        /// </summary>
        /// <param name="container">The container to add it to.</param>
        public Hotkey(IContainer container) : this()
        {
            container.Add(this);
        }

        /// <summary>
        /// Initializes a new instance of this class.
        /// </summary>
        public Hotkey() 
        {
        }

        /// <summary>
        /// Enables the hotkey. When the hotkey is enabled, pressing it causes a
        /// <c>HotkeyPressed</c> event instead of being handled by the active 
        /// application.
        /// </summary>
        public bool Enabled
        {
            get
            {
                return isEnabled;
            }
            set
            {
                isEnabled = value;
                updateHotkey(false);
            }
        }

        /// <summary>
        /// The key code of the hotkey.
        /// </summary>
        public Keys KeyCode
        {
            get
            {
                return _keyCode;
            }

            set
            {
                _keyCode = value;
                updateHotkey(true);
            }
        }

        /// <summary>
        /// Whether the shortcut includes the Control modifier.
        /// </summary>
        public bool Ctrl {
            get { return _ctrl; }
            set {_ctrl = value; updateHotkey(true);}
        }

        /// <summary>
        /// Whether this shortcut includes the Alt modifier.
        /// </summary>
        public bool Alt {
            get { return _alt; }
            set {_alt = value; updateHotkey(true);}
        }   
   
        /// <summary>
        /// Whether this shortcut includes the shift modifier.
        /// </summary>
        public bool Shift {
            get { return _shift; }
            set {_shift = value; updateHotkey(true);}
        }

        /// <summary>
        /// Whether this shortcut includes the shift modifier.
        /// </summary>
        public string strName
        {
            get { return _name; }
            set { _name = value; updateHotkey(true); }
        }

        /// <summary>
        /// Whether this shortcut includes the Windows key modifier. The windows key
        /// is an addition by Microsoft to the keyboard layout. It is located between
        /// Control and Alt and depicts a Windows flag.
        /// </summary>
        public bool WindowsKey {
            get { return _windows; }
            set {_windows = value; updateHotkey(true);}
        }

        /// <summary>
        /// Releases all resources used by the System.ComponentModel.Component.
        /// </summary>
        /// <param name="disposing">Whether to dispose managed resources.</param>
        protected override void Dispose(bool disposing)
        {
            isDisposed = true;
            updateHotkey(false);
            base.Dispose(disposing);
        }

        private string ComboSignature()
        {
            return (int)_keyCode + "|" + _ctrl + "|" + _alt + "|" + _shift + "|" + _windows;
        }

        private void updateHotkey(bool reregister)
        {
            bool shouldBeRegistered = isEnabled && !isDisposed && !DesignMode;
            if (shouldBeRegistered && _hook == null)
            {
                string sig = ComboSignature();
                lock (_staticLock)
                {
                    foreach (var other in _activeInstances)
                    {
                        if (other != this && other.ComboSignature() == sig)
                        {
                            Trace.WriteLine("Hotkey already in use (within this process): " + sig);
                            throw new HotkeyAlreadyInUseException();
                        }
                    }
                }

                var hook = new LowLevelKeyboardHook();
                hook.MessageIntercepted += Hook_MessageIntercepted;
                try
                {
                    hook.StartHook();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("LowLevelKeyboardHook.StartHook FAILED: " + ex);
                    hook.MessageIntercepted -= Hook_MessageIntercepted;
                    hook.Dispose();
                    throw new HotkeyAlreadyInUseException();
                }

                _hook = hook;
                lock (_staticLock)
                {
                    _activeInstances.Add(this);
                }
                Trace.WriteLine("Hotkey started via low-level hook: " + sig);
            }
            else if (!shouldBeRegistered && _hook != null)
            {
                _hook.MessageIntercepted -= Hook_MessageIntercepted;
                _hook.Dispose();
                _hook = null;
                _mainKeyIsDown = false;
                _interceptAltUp = false;
                _altDownHWnd = IntPtr.Zero;
                lock (_staticLock)
                {
                    _activeInstances.Remove(this);
                }
                Trace.WriteLine("Hotkey stopped.");
            }
            // If already running, "reregister" is a no-op: the hook callback reads
            // the current KeyCode/modifier fields live, so property changes take
            // effect immediately without restarting the hook.
        }

        private void Hook_MessageIntercepted(LowLevelMessage evt, ref bool handled)
        {
            if (handled) return;
            var k = evt as LowLevelKeyboardMessage;
            if (k == null) return;

            int vk = k.VirtualKeyCode;
            int msg = k.Message;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (!down && !up) return;

            // The trigger key.
            if (vk == (int)_keyCode)
            {
                if (down && !_mainKeyIsDown && ModifiersMatch())
                {
                    _mainKeyIsDown = true;
                    if (_alt) _interceptAltUp = true;
                    Trace.WriteLine("Hotkey fired: " + ComboSignature());
                    var handler = HotkeyPressed;
                    if (handler != null) handler(this, EventArgs.Empty);
                    handled = true;
                }
                else if (up && _mainKeyIsDown)
                {
                    _mainKeyIsDown = false;
                    handled = true;
                }
                return;
            }

            // Remember which window received the Alt key-down (only that window's
            // thread will track Alt as held). Captured once here, not on each trigger
            // fire, so hold-Alt-and-tap-key cycling repairs the original window.
            if (_alt && down && IsAltVk(vk))
            {
                _altDownHWnd = GetForegroundWindow();
            }

            // Intercept the Alt key-up after a hotkey has fired. The window that had
            // focus when Alt went down needs its key state released without a real
            // WM_SYSKEYUP(VK_MENU) reaching a menu-bearing target window (which is
            // what activates Chrome's 3-dot menu). Always repair the Alt-down window
            // via a non-system WM_KEYUP, and block the real key-up only when focus is
            // on another app's window. Other modifiers (Ctrl/Shift/Win) do not activate
            // menus, so they are left alone.
            if (_interceptAltUp && up && IsAltVk(vk))
            {
                _interceptAltUp = false;
                // Always repair the Alt-down window's key state (a non-system
                // WM_KEYUP releases it without entering menu mode).
                PostKeyUp(_altDownHWnd, vk, k.ScanCode, k.Flags);
                // Block the real key-up only when focus landed on another app's
                // window (a real switch) - that stray Alt key-up is what would
                // activate its menu. When focus is on our own window (the popup),
                // let the real key-up through so releasing Alt confirms selection.
                uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
                if (fgThread != GetCurrentThreadId())
                {
                    handled = true;
                }
                return;
            }
        }

        private static void PostKeyUp(IntPtr hwnd, int vk, int scanCode, int flags)
        {
            if (hwnd == IntPtr.Zero) return;
            bool extended = (flags & 0x01) != 0; // LLKHF_EXTENDEDKEY
            // repeat=1 | scan code | extended | previous state(1) | transition(1=up)
            uint lparam = 1u
                          | ((uint)(scanCode & 0xFF) << 16)
                          | (extended ? (1u << 24) : 0u)
                          | (1u << 30)
                          | (1u << 31);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)lparam);
        }

        private bool ModifiersMatch()
        {
            return _alt == IsKeyDown(VK_MENU)
                && _ctrl == IsKeyDown(VK_CONTROL)
                && _shift == IsKeyDown(VK_SHIFT)
                && _windows == (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN));
        }

        private static bool IsAltVk(int vk)
        {
            return vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;
        }

        private static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        #region PInvoke Declarations

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const int WM_KEYUP = 0x0101;
        private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        #endregion
    }

    /// <summary>
    /// The exception is thrown when a hotkey should be registered that
    /// has already been registered by another application.
    /// </summary>
    public class HotkeyAlreadyInUseException : Exception { }
}
