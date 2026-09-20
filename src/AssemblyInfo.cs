//
//  AssemblyInfo.cs — CodingFire for Windows
//
//  版本号的唯一出处。改版本只改这里。
//
//  为什么不让 build.ps1 去改写源码：那会在编译失败时把工作区留在半改状态。
//  改成「源码里写死 + release.ps1 断言它和 tag 一致」——忘了改版本号就发不出去，
//  不会出现「exe 自称 v1.0.1 而 tag 是 v1.0.2」这种事后才发现的错位。
//
//  运行时由 AppInfo.Version 读出来，托盘 / 控制台 / --dump 三处显示同一个值。
//
//  AssemblyVersion 固定不动：本程序没有强名称、也没有任何程序集绑定，
//  改它只会让「同一个程序换了个版本」在系统看来像换了个程序。
//

using System.Reflection;

[assembly: AssemblyProduct("CodingFire")]
[assembly: AssemblyTitle("CodingFire for Windows")]
[assembly: AssemblyDescription("A campfire that burns tokens, not CPU.")]
[assembly: AssemblyCompany("CodingFire")]
[assembly: AssemblyCopyright("MIT licensed. See LICENSE for details.")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.2")]
[assembly: AssemblyInformationalVersion("1.0.2")]
