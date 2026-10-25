using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1192CollectibleDictionary_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Serializing_a_collectible_dictionary_does_not_keep_its_type_alive(bool collectibleArgument, bool declaredInterface)
        {
            var reference = SerializeCollectibleDictionary(collectibleArgument, declaredInterface);
            for (var attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.False(reference.IsAlive);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference SerializeCollectibleDictionary(bool collectibleArgument, bool declaredInterface)
        {
            var name = new AssemblyName("CollectibleDictionary" + Guid.NewGuid().ToString("N"));
            var assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndCollect);
            var module = assembly.DefineDynamicModule(name.Name);
            var builder = module.DefineType("TransientType", TypeAttributes.Public,
                collectibleArgument ? typeof(object) : typeof(Dictionary<string, int>));
            var type = builder.CreateTypeInfo().AsType();
            var dictionaryType = collectibleArgument ? typeof(Dictionary<,>).MakeGenericType(typeof(string), type) : type;
            var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType);
            if (!collectibleArgument) dictionary.Add("number", 42);
            var declaredType = declaredInterface ? typeof(IDictionary<,>).MakeGenericType(typeof(string), type) : dictionaryType;
            var document = new BsonMapper().Serialize(declaredType, dictionary).AsDocument;
            if (!collectibleArgument) Assert.Equal(42, document["number"].AsInt32);
            else Assert.Empty(document.GetElements());
            return new WeakReference(type);
        }
    }
}
