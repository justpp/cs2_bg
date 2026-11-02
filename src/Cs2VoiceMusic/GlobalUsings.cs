// 全局 using。
// 注意：本项目同时启用了 WPF 与 WinForms（托盘图标需要 NotifyIcon），
// 所以关闭隐式 using，改为在这里显式列出，避免 System.Drawing / System.Windows.Forms
// 与 System.Windows 之间的类型名冲突（Brush、Application、KeyEventArgs 等）。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
