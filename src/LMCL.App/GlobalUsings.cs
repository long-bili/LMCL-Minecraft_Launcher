// WPF 项目的隐式 using 比普通项目少（微软刻意去掉了 System.IO，
// 免得 Path / File 之类跟 System.Windows.Shapes 的名字打架）。
// 但 UI 层到处都要开目录、读日志、写配置，所以这里补回来。
global using System.IO;
