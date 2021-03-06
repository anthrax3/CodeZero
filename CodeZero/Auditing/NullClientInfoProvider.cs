﻿//  <copyright file="NullClientInfoProvider.cs" project="CodeZero" solution="CodeZero">
//      Copyright (c) 2018 CodeZero Framework.  All rights reserved.
//  </copyright>
//  <author>Nasr Aldin M.</author>
//  <email>nasr2ldin@gmail.com</email>
//  <website>https://nasraldin.com/codezero</website>
//  <github>https://nasraldin.github.io/CodeZero</github>
//  <date>01/01/2018 01:00 AM</date>
namespace CodeZero.Auditing
{
    public class NullClientInfoProvider : IClientInfoProvider
    {
        public static NullClientInfoProvider Instance { get; } = new NullClientInfoProvider();

        public string BrowserInfo => null;
        public string ClientIpAddress => null;
        public string ComputerName => null;
    }
}