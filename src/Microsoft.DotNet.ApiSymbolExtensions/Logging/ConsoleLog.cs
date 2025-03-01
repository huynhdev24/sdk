// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.DotNet.ApiSymbolExtensions.Logging
{
    /// <summary>
    /// Class to define common logging abstraction to the console across the APICompat and GenAPI codebases.
    /// </summary>
    public class ConsoleLog : ILog
    {
        private readonly MessageImportance _messageImportance;

        /// <inheritdoc />
        public bool HasLoggedErrors { get; private set; }

        public ConsoleLog(MessageImportance messageImportance) =>
            _messageImportance = messageImportance;

        /// <inheritdoc />
        public virtual void LogError(string message)
        {
            HasLoggedErrors = true;
            Console.Error.WriteLine(message);
        }

        /// <inheritdoc />
        public virtual void LogError(string code, string message)
        {
            HasLoggedErrors = true;
            Console.Error.WriteLine($"{code}: {message}");
        }

        /// <inheritdoc />
        public virtual void LogWarning(string message) =>
            Console.WriteLine(message);

        /// <inheritdoc />
        public virtual void LogWarning(string code, string message) =>
            Console.WriteLine($"{code}: {message}");

        /// <inheritdoc />
        public virtual void LogMessage(string message) =>
            LogMessage(MessageImportance.Normal, message);

        /// <inheritdoc />
        public virtual void LogMessage(MessageImportance importance, string message)
        {
            if (importance > _messageImportance)
                return;

            Console.WriteLine(message);
        }
    }
}
