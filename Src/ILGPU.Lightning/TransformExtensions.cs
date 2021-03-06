﻿// -----------------------------------------------------------------------------
//                              ILGPU.Lightning
//                Copyright (c) 2017-2018 ILGPU Lightning Project
//                                www.ilgpu.net
//
// File: TransformExtensions.cs
//
// This file is part of ILGPU and is distributed under the University of
// Illinois Open Source License. See LICENSE.txt for details.
// -----------------------------------------------------------------------------

using ILGPU.Runtime;
using System;
using System.Reflection;

namespace ILGPU.Lightning
{
    /// <summary>
    /// Represents an abstract interface for a value transformer.
    /// </summary>
    /// <typeparam name="TSource">The source value type of the transformation.</typeparam>
    /// <typeparam name="TTarget">The target value type of the transformation.</typeparam>
    public interface ITransformer<TSource, TTarget>
        where TSource : struct
        where TTarget : struct
    {
        /// <summary>
        /// Transforms the given value of type <typeparamref name="TSource"/>
        /// into a transformed value of type <typeparamref name="TTarget"/>.
        /// </summary>
        /// <param name="value">The value to transform.</param>
        /// <returns>The transformed value of type <typeparamref name="TTarget"/>.</returns>
        TTarget Transform(TSource value);
    }

    /// <summary>
    /// Represents a generic identity transformer.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
    public struct IdentityTransformer<T> : ITransformer<T, T>
        where T : struct
    {
        #region ITransformer

        /// <summary>
        /// Performs an identity transformation by returning the input value.
        /// </summary>
        /// <param name="value">The value to transform.</param>
        /// <returns>The unchanged input value.</returns>
        public T Transform(T value)
        {
            return value;
        }

        #endregion
    }

    /// <summary>
    /// Implements a transformer algorithm.
    /// </summary>
    /// <typeparam name="TSource">The source value type of the transformation.</typeparam>
    /// <typeparam name="TTarget">The target value type of the transformation.</typeparam>
    /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
    static class TransformImpl<TSource, TTarget, TTransformer>
        where TSource : struct
        where TTarget : struct
        where TTransformer : struct, ITransformer<TSource, TTarget>
    {
        /// <summary>
        /// Represents a transform kernel.
        /// </summary>
        public static readonly MethodInfo KernelMethod =
            typeof(TransformImpl<TSource, TTarget, TTransformer>).GetMethod(
                nameof(Kernel),
                BindingFlags.NonPublic | BindingFlags.Static);

        private static void Kernel(
            Index index,
            ArrayView<TSource> source,
            ArrayView<TTarget> target,
            TTransformer transformer)
        {
            var stride = GridExtensions.GridStrideLoopStride;
            for (var idx = index; idx < source.Length; idx += stride)
                target[idx] = transformer.Transform(source[idx]);
        }
    }

    /// <summary>
    /// Represents an element transformer that Transforms elements in the source view into
    /// elements in the target view using the given transformer.
    /// </summary>
    /// <typeparam name="TSource">The source value type of the transformation.</typeparam>
    /// <typeparam name="TTarget">The target value type of the transformation.</typeparam>
    /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
    /// <param name="stream">The accelerator stream.</param>
    /// <param name="source">The source elements to transform</param>
    /// <param name="target">The target elements that will contain the transformed values.</param>
    /// <param name="transformer">The used transformer.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1005:AvoidExcessiveParametersOnGenericTypes", Justification = "Required to realize a generic implementation of a transformation")]
    public delegate void Transformer<TSource, TTarget, TTransformer>(
        AcceleratorStream stream,
        ArrayView<TSource> source,
        ArrayView<TTarget> target,
        TTransformer transformer)
        where TSource : struct
        where TTarget : struct
        where TTransformer : struct, ITransformer<TSource, TTarget>;

    /// <summary>
    /// Transformer functionality for accelerators.
    /// </summary>
    public static class TransformExtensions
    {
        /// <summary>
        /// Creates a raw transformer that is defined by the given source and target type and the specified
        /// transformer type.
        /// </summary>
        /// <typeparam name="TSource">The source value type of the transformation.</typeparam>
        /// <typeparam name="TTarget">The target value type of the transformation.</typeparam>
        /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
        /// <param name="accelerator">The accelerator.</param>
        /// <param name="minDataSize">The minimum data size for maximum occupancy.</param>
        /// <returns>The loaded transformer.</returns>
        private static Action<AcceleratorStream, Index, ArrayView<TSource>, ArrayView<TTarget>, TTransformer> CreateRawTransformer<TSource, TTarget, TTransformer>(
            this Accelerator accelerator,
            out Index minDataSize)
            where TSource : struct
            where TTarget : struct
            where TTransformer : struct, ITransformer<TSource, TTarget>
        {
            var result = accelerator.LoadAutoGroupedKernel<Action<AcceleratorStream, Index, ArrayView<TSource>, ArrayView<TTarget>, TTransformer>>(
                TransformImpl<TSource, TTarget, TTransformer>.KernelMethod, out int groupSize, out int minGridSize);
            minDataSize = groupSize * minGridSize;
            return result;
        }

        /// <summary>
        /// Creates a raw transformer that is defined by the given source and target type and the specified
        /// transformer type.
        /// </summary>
        /// <typeparam name="TSource">The source value type of the transformation.</typeparam>
        /// <typeparam name="TTarget">The target value type of the transformation.</typeparam>
        /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
        /// <param name="accelerator">The accelerator.</param>
        /// <returns>The loaded transformer.</returns>
        public static Transformer<TSource, TTarget, TTransformer> CreateTransformer<TSource, TTarget, TTransformer>(
            this Accelerator accelerator)
            where TSource : struct
            where TTarget : struct
            where TTransformer : struct, ITransformer<TSource, TTarget>
        {
            var rawTransformer = accelerator.CreateRawTransformer<TSource, TTarget, TTransformer>(out Index minDataSize);
            return (stream, source, target, transformer) =>
            {
                if (!source.IsValid)
                    throw new ArgumentNullException(nameof(source));
                if (source.Length < 1)
                    throw new ArgumentOutOfRangeException(nameof(source));
                if (!target.IsValid)
                    throw new ArgumentNullException(nameof(source));
                if (target.Length < source.Length)
                    throw new ArgumentOutOfRangeException(nameof(target), "The source view is larger than the target view");
                rawTransformer(stream, Math.Min(source.Length, minDataSize), source, target, transformer);
            };
        }

        /// <summary>
        /// Creates a new transformer that is defined by the element type and the specified transformer type.
        /// </summary>
        /// <typeparam name="T">The type of the elements to transform.</typeparam>
        /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
        /// <param name="accelerator">The accelerator.</param>
        /// <returns>The loaded transformer.</returns>
        public static Transformer<T, T, TTransformer> CreateTransformer<T, TTransformer>(
            this Accelerator accelerator)
            where T : struct
            where TTransformer : struct, ITransformer<T, T>
        {
            return accelerator.CreateTransformer<T, T, TTransformer>();
        }

        /// <summary>
        /// Transforms elements in the source view into elements in the target view using the given transformer.
        /// </summary>
        /// <typeparam name="T">The type of the elements to transform.</typeparam>
        /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
        /// <param name="accelerator">The accelerator.</param>
        /// <param name="stream">The accelerator stream.</param>
        /// <param name="source">The source elements to transform</param>
        /// <param name="target">The target elements that will contain the transformed values.</param>
        /// <param name="transformer">The used transformer.</param>
        public static void Transform<T, TTransformer>(
            this Accelerator accelerator,
            AcceleratorStream stream,
            ArrayView<T> source,
            ArrayView<T> target,
            TTransformer transformer)
            where T : struct
            where TTransformer : struct, ITransformer<T, T>
        {
            accelerator.CreateTransformer<T, TTransformer>()(
                stream,
                source,
                target,
                transformer);
        }

        /// <summary>
        /// Transforms elements in the source view into elements in the target view using the given transformer.
        /// </summary>
        /// <typeparam name="T">The type of the elements to transform.</typeparam>
        /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
        /// <param name="accelerator">The accelerator.</param>
        /// <param name="source">The source elements to transform</param>
        /// <param name="target">The target elements that will contain the transformed values.</param>
        /// <param name="transformer">The used transformer.</param>
        public static void Transform<T, TTransformer>(
            this Accelerator accelerator,
            ArrayView<T> source,
            ArrayView<T> target,
            TTransformer transformer)
            where T : struct
            where TTransformer : struct, ITransformer<T, T>
        {
            accelerator.Transform<T, TTransformer>(accelerator.DefaultStream, source, target, transformer);
        }

        /// <summary>
        /// Transforms elements in the source view into elements in the target view using the given transformer.
        /// </summary>
        /// <typeparam name="TSource">The source type of the elements to transform.</typeparam>
        /// <typeparam name="TTarget">The target type of the elements that have been transformed.</typeparam>
        /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
        /// <param name="accelerator">The accelerator.</param>
        /// <param name="stream">The accelerator stream.</param>
        /// <param name="source">The source elements to transform</param>
        /// <param name="target">The target elements that will contain the transformed values.</param>
        /// <param name="transformer">The used transformer.</param>
        public static void Transform<TSource, TTarget, TTransformer>(
            this Accelerator accelerator,
            AcceleratorStream stream,
            ArrayView<TSource> source,
            ArrayView<TTarget> target,
            TTransformer transformer)
            where TSource : struct
            where TTarget : struct
            where TTransformer : struct, ITransformer<TSource, TTarget>
        {
            accelerator.CreateTransformer<TSource, TTarget, TTransformer>()(
                stream,
                source,
                target,
                transformer);
        }

        /// <summary>
        /// Transforms elements in the source view into elements in the target view using the given transformer.
        /// </summary>
        /// <typeparam name="TSource">The source type of the elements to transform.</typeparam>
        /// <typeparam name="TTarget">The target type of the elements that have been transformed.</typeparam>
        /// <typeparam name="TTransformer">The transformer to transform elements from the source type to the target type.</typeparam>
        /// <param name="accelerator">The accelerator.</param>
        /// <param name="source">The source elements to transform</param>
        /// <param name="target">The target elements that will contain the transformed values.</param>
        /// <param name="transformer">The used transformer.</param>
        public static void Transform<TSource, TTarget, TTransformer>(
            this Accelerator accelerator,
            ArrayView<TSource> source,
            ArrayView<TTarget> target,
            TTransformer transformer)
            where TSource : struct
            where TTarget : struct
            where TTransformer : struct, ITransformer<TSource, TTarget>
        {
            accelerator.Transform(accelerator.DefaultStream, source, target, transformer);
        }
    }
}
