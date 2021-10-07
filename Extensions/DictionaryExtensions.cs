using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public static class DictionaryExtensions
    {
        /// <summary>
        /// Adds a key and value to the dictionary. If the key already exists, the value will be updated using the <paramref name="existingKeyUpdater"/> function.
        /// </summary>
        /// <typeparam name="TKey">The type of dictionary key.</typeparam>
        /// <typeparam name="TValue">The type of dictionary value.</typeparam>
        /// <param name="dictionary">The dictionary to add or update the value in.</param>
        /// <param name="key">The key to add.</param>
        /// <param name="value">The value to add.</param>
        /// <param name="existingKeyUpdater">A function that takes an existing value from the dictionary and updates it with the specified <paramref name="value"/>.</param>
        /// <returns>The value added to the dictionary.</returns>
        public static TValue AddOrUpdate<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value, Func<TValue, TValue, TValue> existingKeyUpdater)
            where TKey : notnull
        {
            // If we have an existing key, run the updater
            if (dictionary.TryGetValue(key, out var existingVal))
            {
                var newValue = existingKeyUpdater(existingVal, value);
                dictionary[key] = newValue;
                return newValue;
            }
            
            // Otherwise, just add the key/value pair to the dictionary.
            dictionary.Add(key, value);
            return value;
        }

        /// <summary>
        /// Adds a key and value to the dictionary whose values are lists. If the key already exists, the value will be added to the list.
        /// </summary>
        /// <typeparam name="TKey">The type of dictionary key.</typeparam>
        /// <typeparam name="TValue">The type of dictionary list value.</typeparam>
        /// <param name="dictionary">The dictionary to add or update the value in.</param>
        /// <param name="key">The key to add.</param>
        /// <param name="value">The value to add to the list.</param>
        /// <returns>The value added to the dictionary.</returns>
        public static List<TValue> AddOrUpdate<TKey, TValue>(this Dictionary<TKey, List<TValue>> dictionary, TKey key, TValue value)
            where TKey : notnull
        {
            // If we have an existing key, run the updater
            if (dictionary.TryGetValue(key, out var existingVal))
            {
                existingVal.Add(value);
                return existingVal;
            }

            // Otherwise, just add key/value pair to the dictionary.
            var newList = new List<TValue> { value };
            dictionary.Add(key, newList);
            return newList;
        }
    }
}
