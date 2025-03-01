﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.Extensions.Tools.Internal
{
    /// <summary>
    /// This API supports infrastructure and is not intended to be used
    /// directly from your code. This API may change or be removed in future releases.
    /// </summary>
    internal interface IReporter
    {
        public bool IsVerbose => false;
        void Verbose(string message, string emoji = "⌚");
        void Output(string message, string emoji = "⌚");
        void Warn(string message, string emoji = "⌚");
        void Error(string message, string emoji = "❌");
    }
}
