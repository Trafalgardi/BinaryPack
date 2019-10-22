﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BinaryPack.Serialization.Constants;
using BinaryPack.Serialization.Processors.Abstract;
using BinaryPack.Serialization.Reflection;

namespace BinaryPack.Serialization.Processors
{
    /// <summary>
    /// A <see langword="class"/> responsible for creating the serializers and deserializers for generic models
    /// </summary>
    /// <typeparam name="T">The type of items to handle during serialization and deserialization</typeparam>
    internal sealed partial class ObjectProcessor<T> : TypeProcessor<T> where T : new()
    {
        /// <summary>
        /// Gets the singleton <see cref="ObjectProcessor{T}"/> instance to use
        /// </summary>
        public static ObjectProcessor<T> Instance { get; } = new ObjectProcessor<T>();

        /// <inheritdoc/>
        protected override void EmitSerializer(ILGenerator il)
        {
            /* Perform a null check only if the type is a reference type.
             * In this case, a single byte will be written to the target stream,
             * with a value of 0 if the input item is null, and 1 otherwise. */
            if (!typeof(T).IsValueType)
            {
                // writer.Write(obj != null);
                il.EmitLoadArgument(Arguments.Write.RefBinaryWriter);
                il.EmitLoadArgument(Arguments.Write.T);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Cgt_Un);
                il.EmitCall(KnownMembers.BinaryWriter.WriteT(typeof(bool)));

                // if (obj == null) return;
                Label isNotNull = il.DefineLabel();
                il.EmitLoadArgument(Arguments.Write.T);
                il.Emit(OpCodes.Brtrue_S, isNotNull);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(isNotNull);
            }

            // Properties serialization
            foreach (PropertyInfo property in
                from prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                where prop.CanRead && prop.CanWrite
                select prop)
            {
                /* First special case, for unmanaged value types.
                 * Here we can just copy the property value directly to a
                 * local buffer of the right size, then cast it to a ReadOnlySpan<byte>
                 * span and write it to the target stream. No particular care is required. */
                if (property.PropertyType.IsUnmanaged())
                {
                    // writer.Write(obj.Property);
                    il.EmitLoadArgument(Arguments.Write.RefBinaryWriter);
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.EmitCall(KnownMembers.BinaryWriter.WriteT(property.PropertyType));
                }
                else if (property.PropertyType == typeof(string))
                {
                    /* Second special case, for string values. Here we just need to
                     * load the string property and then invoke the string processor, which
                     * will handle all the possible cases like null values, empty strings, etc. */
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.EmitLoadArgument(Arguments.Write.RefBinaryWriter);
                    il.EmitCall(StringProcessor.Instance.SerializerInfo.MethodInfo);
                }
                else if (property.PropertyType.IsArray)
                {
                    /* Third special case, for array types. Like with strings, we only need
                     * to load the property valaue and then delegate the rest of the
                     * serialization to the appropriate ArrayProcessor<T> instance, which
                     * is retrieved through reflection from the type of elements in the current property. */
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.EmitLoadArgument(Arguments.Write.RefBinaryWriter);
                    il.EmitCall(KnownMembers.TypeProcessor.SerializerInfo(typeof(ArrayProcessor<>), property.PropertyType.GetElementType()));
                }
                else if (property.PropertyType.IsGenericType &&
                         property.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    /* Fourth special case, for List<T> types. In this case we just need to get
                     * the property value and leave the rest of the work to ListProcessor<T>. */
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.EmitLoadArgument(Arguments.Write.RefBinaryWriter);
                    il.EmitCall(KnownMembers.TypeProcessor.SerializerInfo(typeof(ListProcessor<>), property.PropertyType.GenericTypeArguments[0]));
                }
                else if (property.PropertyType.IsInterface &&
                         property.PropertyType.IsGenericType &&
                         (property.PropertyType.GetGenericTypeDefinition() == typeof(IList<>) ||
                          property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
                          property.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>) ||
                          property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)))
                {
                    /* Fifth special case, for generic interface types. This case only applies to properties
                     * of one of the generic interfaces mentioned above, and it includes two fast paths and a
                     * fallback path. The fast paths are for List<T> values, which are serialized with the
                     * ListProcessor<T> type, and for T[] values, which just use the ArrayProcessor<T> type.
                     * All other values fallback to the IEnumerableProcessor<T> type. */
                    Label
                        isNotList = il.DefineLabel(),
                        fallback = il.DefineLabel(),
                        propertyHandled = il.DefineLabel();

                    // if (obj.Property is List<T> list) ListProcessor<T>.Instance.Serializer(list, stream);
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.Emit(OpCodes.Isinst, typeof(List<>).MakeGenericType(property.PropertyType.GenericTypeArguments[0]));
                    il.Emit(OpCodes.Brfalse_S, isNotList);
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.EmitLoadArgument(Arguments.Write.RefBinaryWriter);
                    il.EmitCall(KnownMembers.TypeProcessor.SerializerInfo(typeof(ListProcessor<>), property.PropertyType.GenericTypeArguments[0]));
                    il.Emit(OpCodes.Br_S, propertyHandled);

                    // else if (obj.Property is T[] array) ArrayProcessor<T>.Instance.Serializer(array, stream);
                    il.MarkLabel(isNotList);
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.Emit(OpCodes.Isinst, property.PropertyType.GenericTypeArguments[0].MakeArrayType());
                    il.Emit(OpCodes.Brfalse_S, fallback);
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.EmitLoadArgument(Arguments.Write.RefBinaryWriter);
                    il.EmitCall(KnownMembers.TypeProcessor.SerializerInfo(typeof(ArrayProcessor<>), property.PropertyType.GenericTypeArguments[0]));
                    il.Emit(OpCodes.Br_S, propertyHandled);

                    // else IEnumerableProcessor<T>.Instance.Serializer(obj.Property, stream);
                    il.MarkLabel(fallback);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Throw); // TODO

                    il.MarkLabel(propertyHandled);
                }
                else
                {
                    // Just use another ObjectProcessor<T> instance for all other property types
                    il.EmitLoadArgument(Arguments.Write.T);
                    il.EmitReadMember(property);
                    il.EmitLoadArgument(Arguments.Write.RefBinaryWriter);
                    il.EmitCall(KnownMembers.TypeProcessor.SerializerInfo(typeof(ObjectProcessor<>), property.PropertyType));
                }
            }

            il.Emit(OpCodes.Ret);
        }

        /// <inheritdoc/>
        protected override void EmitDeserializer(ILGenerator il)
        {
            // T obj; ...;
            il.DeclareLocal(typeof(T));

            /* Initial null reference check for reference types.
             * If the first byte in the stream is 0, just return null. */
            if (!typeof(T).IsValueType)
            {
                // if (!reader.Read<bool>()) return null;
                Label isNotNull = il.DefineLabel();
                il.EmitLoadArgument(Arguments.Read.RefBinaryReader);
                il.EmitCall(KnownMembers.BinaryReader.ReadT(typeof(bool)));
                il.Emit(OpCodes.Brtrue_S, isNotNull);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(isNotNull);

                // T obj = new T();
                il.Emit(OpCodes.Newobj, typeof(T).GetConstructor(Type.EmptyTypes));
                il.EmitStoreLocal(Locals.Read.T);
            }
            else
            {
                // T obj = default;
                il.EmitLoadLocalAddress(Locals.Read.T);
                il.Emit(OpCodes.Initobj, typeof(T));
            }

            // Deserialize all the contained properties
            foreach (PropertyInfo property in
                from prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                where prop.CanRead && prop.CanWrite
                select prop)
            {
                /* Just like with the serialization pass, handle each case separately.
                 * If the property is of an unmanaged type, just read the bytes from the
                 * stream and assign the target property by reinterpreting them to the right type. */
                if (property.PropertyType.IsUnmanaged())
                {
                    // obj.Property = reader.Read<TProperty>();
                    il.EmitLoadLocal(Locals.Read.T);
                    il.EmitLoadArgument(Arguments.Read.RefBinaryReader);
                    il.EmitCall(KnownMembers.BinaryReader.ReadT(property.PropertyType));
                    il.EmitWriteMember(property);
                }
                else if (property.PropertyType == typeof(string))
                {
                    // Invoke StringProcessor to read the string property
                    il.EmitLoadLocal(Locals.Read.T);
                    il.EmitLoadArgument(Arguments.Read.RefBinaryReader);
                    il.EmitCall(StringProcessor.Instance.DeserializerInfo.MethodInfo);
                    il.EmitWriteMember(property);
                }
                else if (property.PropertyType.IsArray)
                {
                    // Invoke ArrayProcessor<T> to read the TItem[] array
                    il.EmitLoadLocal(Locals.Read.T);
                    il.EmitLoadArgument(Arguments.Read.RefBinaryReader);
                    il.EmitCall(KnownMembers.TypeProcessor.DeserializerInfo(typeof(ArrayProcessor<>), property.PropertyType.GetElementType()));
                    il.EmitWriteMember(property);
                }
                else if (property.PropertyType.IsGenericType &&
                         property.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    // Invoke ListProcessor<T> to read the List<T> list
                    il.EmitLoadLocal(Locals.Read.T);
                    il.EmitLoadArgument(Arguments.Read.RefBinaryReader);
                    il.EmitCall(KnownMembers.TypeProcessor.DeserializerInfo(typeof(ListProcessor<>), property.PropertyType.GenericTypeArguments[0]));
                    il.EmitWriteMember(property);
                }
                else if (property.PropertyType.IsInterface &&
                         property.PropertyType.IsGenericType &&
                         (property.PropertyType.GetGenericTypeDefinition() == typeof(IList<>) ||
                          property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
                          property.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>) ||
                          property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)))
                {
                    /* When deserializing a property of one of these interface types, the ListProcessor<T> is
                     * always used, regardless of the actual underlying type that the property originally had
                     * during the serialization pass (eg. it could have been a T[] array, or a HashSet<T>). */
                    il.EmitLoadLocal(Locals.Read.T);
                    il.EmitLoadArgument(Arguments.Read.RefBinaryReader);
                    il.EmitCall(KnownMembers.TypeProcessor.DeserializerInfo(typeof(ListProcessor<>), property.PropertyType.GenericTypeArguments[0]));
                    il.EmitWriteMember(property);
                }
                else
                {
                    // Fallback to another ObjectProcessor<T> for all other types
                    il.EmitLoadLocal(Locals.Read.T);
                    il.EmitLoadArgument(Arguments.Read.RefBinaryReader);
                    il.EmitCall(KnownMembers.TypeProcessor.DeserializerInfo(typeof(ObjectProcessor<>), property.PropertyType));
                    il.EmitWriteMember(property);
                }
            }

            // return obj;
            il.EmitLoadLocal(Locals.Read.T);
            il.Emit(OpCodes.Ret);
        }
    }
}
