﻿//  <copyright file="IBackgroundWorker.cs" project="CodeZero" solution="CodeZero">
//      Copyright (c) 2018 CodeZero Framework.  All rights reserved.
//  </copyright>
//  <author>Nasr Aldin M.</author>
//  <email>nasr2ldin@gmail.com</email>
//  <website>https://nasraldin.com/codezero</website>
//  <github>https://nasraldin.github.io/CodeZero</github>
//  <date>01/01/2018 01:00 AM</date>
namespace CodeZero.Threading.BackgroundWorkers
{
    /// <summary>
    /// Interface for a worker (thread) that runs on background to perform some tasks.
    /// </summary>
    public interface IBackgroundWorker : IRunnable
    {

    }
}