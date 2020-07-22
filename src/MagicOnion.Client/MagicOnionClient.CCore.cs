#if !NON_UNITY || USE_GRPC_CCORE
using System;
using System.Collections.Generic;
using System.Text;
using Grpc.Core;
using MessagePack;

namespace MagicOnion.Client
{
    public static partial class MagicOnionClient
    {
        public static T Create<T>(Channel channel)
            where T : IService<T>
        {
            return Create<T>(new DefaultCallInvoker(channel), MessagePackSerializer.DefaultOptions, emptyFilters);
        }

        public static T Create<T>(Channel channel, IClientFilter[] clientFilters)
            where T : IService<T>
        {
            return Create<T>(new DefaultCallInvoker(channel), MessagePackSerializer.DefaultOptions, clientFilters);
        }

        public static T Create<T>(Channel channel, MessagePackSerializerOptions serializerOptions)
            where T : IService<T>
        {
            return Create<T>(new DefaultCallInvoker(channel), serializerOptions, emptyFilters);
        }
    }
}
#endif