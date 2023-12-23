#if UNITY_EDITOR
using Unity.Collections;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Serialization.Json;

namespace Unity.Entities.Serialization
{
    public static partial class SerializeUtility
    {
        #region World Yaml Serialization

        /// <summary>
        /// Serialize the given World to a YAML file, for logging and analysis purpose
        /// </summary>
        /// <param name="entityManager">Entity Manager of the World to serialize</param>
        /// <param name="writer">The stream we will write the data to</param>
        /// <param name="dumpChunkRawData">If <value>true</value> the binary data of each chunk section (header, component data of each entity) will be saved.
        /// This will increase the volume of data being written drastically, but you will be able to diff at the binary level
        /// </param>
        /// <remarks>
        /// Analysing a serialized sub-scene for instance is not easy because the data is raw binary. It is even harder if we want to compare two distinct serialization of the same sub-scene.
        /// This method will allow us to save the data of a given World in YAML.
        /// Note that so far the data being saved is not totally complete, it will improve over time.
        /// </remarks>
        public static unsafe void SerializeWorldIntoYAML(EntityManager entityManager, StreamWriter writer, bool dumpChunkRawData)
        {
            if (writer.NewLine != "\n")
            {
                throw new ArgumentException("YAML World serialization must be done with a line ending being \\n on all platforms", nameof(writer));
            }

            var yaml = new YamlWriter(writer);
            WriteYAMLHeader(yaml);
            WriteArchetypes(yaml, entityManager);

            var access = entityManager.GetCheckedEntityDataAccess();
            var entityComponentStore = access->EntityComponentStore;

            using (var archetypeArray = GetAllArchetypes(entityComponentStore, Allocator.Temp))
            using (var entityRemapInfos = new NativeArray<EntityRemapUtility.EntityRemapInfo>(entityManager.EntityCapacity, Allocator.Temp))
            using (yaml.WriteCollection(k_ChunksDataCollectionTag))
            {
                GenerateRemapInfo(entityManager, archetypeArray, entityRemapInfos);
                for (int a = 0; a < archetypeArray.Length; ++a)
                {
                    var archetype = archetypeArray.Ptr[a];
                    using (yaml.WriteCollection(k_ArchetypeCollectionTag))
                    {
                        yaml.WriteKeyValue("name", archetype->ToString());

                        for (var ci = 0; ci < archetype->Chunks.Count; ++ci)
                        {
                            var chunk = archetype->Chunks[ci];
                            WriteChunkData(yaml, entityManager, entityRemapInfos, chunk, archetype, a, dumpChunkRawData);
                        }
                    }
                }
            }
        }

        #region Tags

        const string k_ArchetypesCollectionTag = "Archetypes";
        const string k_ArchetypeCollectionTag = "Archetype";
        const string k_ChunksDataCollectionTag = "ChunksData";
        const string k_ChunkDataCollectionTag = "ChunkData";
        const string k_ComponentDataCollectionTag = "ComponentDataCollection";
        const string k_ComponentDataTag = "ComponentData";

        #endregion

        #region Data type field info extraction

        /// <summary>
        /// Helper class that will build for a given type a list of all its data fields with the required information for us to dump these fields data later on
        /// </summary>
        internal static class TypeDataExtractor
        {
            [DebuggerDisplay("Type = {" + nameof(Type) + "}")]
            [DebuggerTypeProxy(typeof(TypeExtractedInfoDebugView))]
            public class TypeExtractedInfo
            {
                public Type Type { get; }
                public FieldExtractedInfo[] Fields { get; }

                public TypeExtractedInfo(Type type)
                {
                    Type = type;
                    Fields = type.GetFields().Select(field => new FieldExtractedInfo(field.Name, field.FieldType, UnsafeUtility.GetFieldOffset(field))).OrderBy(field => field.Name).ToArray();
                }
            }

            sealed class TypeExtractedInfoDebugView
            {
                TypeExtractedInfo Owner;

                public TypeExtractedInfoDebugView(TypeExtractedInfo obj)
                {
                    Owner = obj;
                }

                [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                public FieldExtractedInfo[] Items => Owner.Fields;
            }

            [DebuggerDisplay("Name = {Name}, Offset = {Offset}, Type = {Type}")]
            public class FieldExtractedInfo
            {
                public FieldExtractedInfo(string name, Type type, int offset)
                {
                    Name = name;
                    Type = type;
                    Offset = offset;
                    Size = UnsafeUtility.SizeOf(Type);
                }

                public string Name { get; }
                public Type Type { get; }
                public int Offset { get; }
                public int Size { get; }
            }

            public static TypeExtractedInfo GetTypeExtractedInfo(Type type) => Types.GetOrAdd(type, t => new TypeExtractedInfo(t));

            static ConcurrentDictionary<Type, TypeExtractedInfo> Types = new ConcurrentDictionary<Type, TypeExtractedInfo>();
        }

        #endregion

        #region Internal implementation

        struct EntityInfo
        {
            public EntityInfo(Entity entity, EntityInChunk entityInChunk)
            {
                Entity = entity;
                EntityInChunk = entityInChunk;
            }

            public Entity Entity;
            public EntityInChunk EntityInChunk;
        }

        static void WriteYAMLHeader(YamlWriter writer)
        {
            if (writer.CurrentIndent != 0)
            {
                throw new InvalidOperationException("The header can only be written as root element");
            }

            writer.WriteLine(@"%YAML 1.1")
                .WriteLine(@"---")
                .WriteLine(@"# ECS Debugging file");
        }

        static unsafe void WriteArchetypes(YamlWriter writer, EntityManager entityManager)
        {
            var access = entityManager.GetCheckedEntityDataAccess();
            var entityComponentStore = access->EntityComponentStore;

            using (var archetypeArray = GetAllArchetypes(entityComponentStore, Allocator.Temp))
            using (writer.WriteCollection(k_ArchetypesCollectionTag))
            {
                for (int i = 0; i != archetypeArray.Length; i++)
                {
                    var a = archetypeArray.Ptr[i];
                    using (writer.WriteCollection(k_ArchetypeCollectionTag))
                    {
                        writer.WriteKeyValue("name", a->ToString())
                            .WriteKeyValue(nameof(Archetype.TypesCount), a->TypesCount)
                            .WriteKeyValue(nameof(EntityArchetype.ChunkCount), a->Chunks.Count)
                            .WriteKeyValue(nameof(EntityArchetype.ChunkCapacity), a->ChunkCapacity);

                        var props = new List<string>();
                        if (a->CleanupComplete)     props.Add(nameof(Archetype.CleanupComplete));
                        if (a->CleanupNeeded)       props.Add(nameof(Archetype.CleanupNeeded));
                        if (a->Disabled)                       props.Add(nameof(Archetype.Disabled));
                        if (a->Prefab)                         props.Add(nameof(Archetype.Prefab));
                        if (a->HasChunkComponents)             props.Add(nameof(Archetype.HasChunkComponents));
                        if (a->HasChunkHeader)                 props.Add(nameof(Archetype.HasChunkHeader));
                        if (a->HasBlobAssetRefs)          props.Add(nameof(Archetype.HasBlobAssetRefs));
                        writer.WriteInlineSequence("properties", props);
                    }
                }
            }
        }

        static unsafe void WriteChunkData(YamlWriter writer, EntityManager entityManager, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapInfos, Chunk* initialChunk, Archetype* archetype, int archetypeIndex, bool dumpChunkRawData)
        {
            var tempChunkMem = stackalloc byte[Chunk.kChunkSize];
            Chunk* tempChunk = (Chunk*)tempChunkMem;
            if (dumpChunkRawData)
            {
                UnsafeUtility.MemCpy(tempChunk, initialChunk, Chunk.kChunkSize);
                tempChunk->ChunkstoreIndex = 0;
                
                byte* tempChunkBuffer = tempChunk->Buffer;

                BufferHeader.PatchAfterCloningChunk(tempChunk);
                EntityRemapUtility.PatchEntities(archetype->ScalarEntityPatches, archetype->ScalarEntityPatchCount, archetype->BufferEntityPatches, archetype->BufferEntityPatchCount, tempChunkBuffer, tempChunk->Count, ref entityRemapInfos);
                ClearChunkHeaderComponents(tempChunk);
                ChunkDataUtility.MemsetUnusedChunkData(tempChunk, 0);

                tempChunk->Archetype = (Archetype*)archetypeIndex;
            }

            using (writer.WriteCollection(k_ChunkDataCollectionTag))
            {
                using (writer.WriteCollection("Header"))
                {
                    WriteEntity(writer, nameof(Chunk.metaChunkEntity), initialChunk->metaChunkEntity);
                    writer.WriteKeyValue(nameof(Chunk.Capacity), initialChunk->Capacity);
                    writer.WriteKeyValue(nameof(Chunk.Count), initialChunk->Count);

                    if (dumpChunkRawData)
                    {
                        writer.WriteFormattedBinaryData("Header-RawData", tempChunk, Chunk.kBufferOffset);
                    }
                }

                // First pass to sort by component type
                var entitiesByChunkIndex = new Entity[initialChunk->Count];
                var componentDataList = new List<int>();
                var chunkComponentDataList = new List<int>();
                var chunkTypes = archetype->Types;
                for (int typeI = 0; typeI < archetype->TypesCount; typeI++)
                {
                    var componentType = &chunkTypes[typeI];
                    var type = TypeManager.GetType(componentType->TypeIndex);
                    ref readonly var typeInfo = ref TypeManager.GetTypeInfo(componentType->TypeIndex);

                    if (componentType->IsChunkComponent)
                    {
                        chunkComponentDataList.Add(typeI);
                    }
                    // Is it a Component Data ?
                    else if (typeof(IComponentData).IsAssignableFrom(type) || typeof(Entity).IsAssignableFrom(type) || typeof(IBufferElementData).IsAssignableFrom(type))
                    {
                        // Ignore Tag Component, no data to dump
                        if (typeInfo.IsZeroSized)    continue;

                        if (typeof(Entity).IsAssignableFrom(type))
                        {
                            componentDataList.Insert(0, typeI);

                            for (int i = 0; i < initialChunk->Count;)
                            {
                                var entity = *(Entity*)(initialChunk->Buffer + archetype->SizeOfs[0] * i);
                                Assert.IsTrue(entityManager.Exists(entity));

                                entitiesByChunkIndex[i] = entity;
                                i++;
                            }
                        }
                        else
                        {
                            componentDataList.Add(typeI);
                        }
                    }
                }

                // Parse the Component Data for this chunk and store them
                using (writer.WriteCollection(k_ComponentDataCollectionTag))
                {
                    var access = entityManager.GetCheckedEntityDataAccess();
                    foreach (var typeI in componentDataList)
                    {
                        var componentTypeInArchetype = &chunkTypes[typeI];
                        var componentType = TypeManager.GetType(componentTypeInArchetype->TypeIndex);

                        ref readonly var componentTypeInfo = ref TypeManager.GetTypeInfo(componentTypeInArchetype->TypeIndex);
                        TypeDataExtractor.TypeExtractedInfo componentExtractedInfo = null;
                        if (UnsafeUtility.IsUnmanaged(componentType))
                            componentExtractedInfo = TypeDataExtractor.GetTypeExtractedInfo(componentType);

                        using (writer.WriteCollection(k_ComponentDataTag))
                        {
                            writer.WriteInlineMap("info", new[]
                            {
                                new System.Collections.Generic.KeyValuePair<object, object>(nameof(System.Type), componentType.Name),
                                new System.Collections.Generic.KeyValuePair<object, object>(nameof(TypeManager.TypeInfo.SizeInChunk), componentTypeInfo.SizeInChunk)
                            });

                            using (writer.WriteCollection("Entities"))
                            {
                                var indexInTypeArray = ChunkDataUtility.GetIndexInTypeArray(archetype, componentTypeInArchetype->TypeIndex);
                                var componentOffsetInChunk = archetype->Offsets[indexInTypeArray];
                                var componentSize = archetype->SizeOfs[indexInTypeArray];
                                var componentsBuffer = initialChunk->Buffer + componentOffsetInChunk;
                                var entityData = new Dictionary<string, string>();

                                // Dump all entities in this chunk
                                for (int i = 0; i < entitiesByChunkIndex.Length; i++)
                                {
                                    var entity = entitiesByChunkIndex[i];
                                    entityData.Clear();

                                    // Get the location of the component data
                                    var compData = componentsBuffer + i * componentSize;

                                    // If the component we are dumping is a Dynamic Buffer
                                    if (typeof(IBufferElementData).IsAssignableFrom(componentType))
                                    {
                                        var header = (BufferHeader*)compData;
                                        var begin = BufferHeader.GetElementPointer(header);
                                        var size = componentTypeInfo.ElementSize;

                                        using (writer.WriteCollection(entity.ToString()))
                                        {
                                            writer.WriteLine($"Length: {header->Length} Capacity: {header->Capacity}");

                                            for (var it = 0; it < header->Length; it++)
                                            {
                                                var item = begin + (size * it);
                                                entityData.Clear();

                                                // Dump each field of the current entity's component data
                                                foreach (var componentFieldInfo in componentExtractedInfo.Fields)
                                                {
                                                    var compDataObject = Marshal.PtrToStructure((IntPtr)item + componentFieldInfo.Offset, componentFieldInfo.Type);
                                                    entityData.Add(componentFieldInfo.Name, compDataObject.ToString());
                                                }
                                                writer.WriteInlineMap($"{it:0000}", entityData);
                                            }
                                        }

                                        if (dumpChunkRawData)
                                        {
                                            if (componentSize > 0)
                                                writer.WriteFormattedBinaryData("ComponentRawData", begin, size * header->Length, 0);
                                        }
                                    }
                                    else
                                    {
                                        // If it's a Component Data
                                        if (componentTypeInfo.Category == TypeManager.TypeCategory.ComponentData && !componentTypeInfo.Type.IsClass || componentTypeInfo.Category == TypeManager.TypeCategory.EntityData)
                                        {
                                            // Dump each field of the current entity's component data
                                            foreach (var componentFieldInfo in componentExtractedInfo.Fields)
                                            {
                                                var compDataObject = Marshal.PtrToStructure((IntPtr)compData + componentFieldInfo.Offset, componentFieldInfo.Type);
                                                entityData.Add(componentFieldInfo.Name, compDataObject.ToString());
                                            }
                                            writer.WriteInlineMap(entity.ToString(), entityData);
                                        }
                                        else if (componentTypeInfo.Category == TypeManager.TypeCategory.ComponentData && componentTypeInfo.Type.IsClass)
                                        {
                                            var obj = access->ManagedComponentStore.GetManagedComponent(*(int*)(compData));
                                            if (obj == null)
                                                writer.WriteLine("null");
                                            else
                                                writer.WriteLine(JsonSerialization.ToJson(obj));
                                        }
                                        //@Todo: Shared components

                                        if (dumpChunkRawData)
                                        {
                                            if (componentSize > 0)
                                                writer.WriteFormattedBinaryData("ComponentRawData", compData, componentSize, 0);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (dumpChunkRawData)
                    {
                        writer.WriteLine("Archetype: " + initialChunk->Archetype->ToString());

                        using (var bufferPatches = new NativeList<BufferPatchRecord>(128, Allocator.Temp))
                        using (var bufferPtrs = new NativeList<IntPtr>(128, Allocator.Temp))
                        {
                            tempChunk->Archetype = archetype;
                            FillBufferPatchRecordsAndClearBufferPointer(tempChunk, bufferPatches, bufferPtrs);
                            for (int i = 0; i < bufferPtrs.Length; ++i)
                            {
                                var ptr = (void*)bufferPtrs[i];
                                BufferHeader.FreeBufferPtr(ptr);
                            }
                        }

                        for (int i = 0; i != tempChunk->Archetype->TypesCount; i++)
                        {
                            var begin = archetype->Offsets[i];
                            var end = i == tempChunk->Archetype->TypesCount -1 ? Chunk.GetChunkBufferSize() : archetype->Offsets[i+1];
                            writer.WriteLine("Type: " + tempChunk->Archetype->Types[i]);
                            writer.WriteFormattedBinaryData("Chunk Row", tempChunk->Buffer + begin, end - begin, 0);
                        }

                        //writer.WriteLine("Archetype: " + initialChunk->Archetype->ToString());
                        //writer.WriteFormattedBinaryData("Complete chunk", tempChunk, Chunk.kChunkSize, 0);
                    }
                }
            }
        }

        static void WriteEntity(YamlWriter writer, string name, Entity entity)
        {
            writer.WriteInlineMap(name, new[] { new System.Collections.Generic.KeyValuePair<object, object>("index", entity.Index), new System.Collections.Generic.KeyValuePair<object, object>("version", entity.Version) });
        }

        #endregion

        #endregion
    }
}

#endif //UNITY_EDITOR
