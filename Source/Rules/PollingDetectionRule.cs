﻿// Copyright (c) Microsoft Corporation
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// 
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace XboxLiveTrace
{
    internal class PollingDetectionRule : IRule
    {
        private float m_sameDeltaThresholdPercentage = Constants.PollingDetectionSameDeltaThresholdMs;
        private float m_maxFrequencyMs = Constants.PollingDetectionMaxFrequencyMs;
        private int m_minSequenceSize = Constants.PollingDetectionMinSequenceSize;
        
        public PollingDetectionRule() : base(Constants.PollDetection)
        {
        }

        class CallDelta
        {
            public ServiceCallItem m_first, m_second;

            public CallDelta m_previous = null;
            public CallDelta m_next = null;

            public UInt64 Delta
            {
                get { return m_second.m_reqTimeUTC - m_first.m_reqTimeUTC; }
            }

            public float DeltaAsMs
            {
                get { return Delta / TimeSpan.TicksPerMillisecond; }
            }

            public override string ToString()
            {
                return String.Format("[{0}, {1}]: {2}", m_first.m_id, m_second.m_id, Delta);
            }
        }

        class Sequence
        {
            public List<ServiceCallItem> m_seqCalls = new List<ServiceCallItem>();
            public double m_averageDelta;

            static public Sequence Create(CallDelta delta)
            {
                Sequence s = new Sequence();
                // Walk through the sequence of deltas starting at the supplied delta
                while (delta != null)
                {
                    s.m_seqCalls.Add(delta.m_first);
                    s.m_averageDelta += delta.Delta;

                    // At this point we need to add the last call to the sequence
                    if (delta.m_next == null)
                    {
                        // Add the call after computing the average because there is 1 less delta than calls
                        s.m_averageDelta /= s.m_seqCalls.Count;
                        s.m_seqCalls.Add(delta.m_second);
                    }

                    delta = delta.m_next;
                }

                return s;
            }

            public bool Contains(Sequence seq)
            {
                // Can't contain something longer than yourself
                if (seq.Length >= Length) return false;

                for (int myIter = 0, otherIter = 0; myIter < m_seqCalls.Count - seq.m_seqCalls.Count + otherIter; ++myIter)
                {
                    // If we found the call, move forward
                    if (m_seqCalls[myIter] == seq.m_seqCalls[otherIter])
                    {
                        // If we are at the end, then the sequence is indeed contained
                        if (++otherIter == seq.m_seqCalls.Count)
                        {
                            return true;
                        }
                    }
                    // Because the sequence is sorted, if this is true the the sequence cannot be contained
                    else if (m_seqCalls[myIter].m_id > seq.m_seqCalls[otherIter].m_id)
                    {
                        return false;
                    }
                }

                return false;
            }

            public int Length
            {
                get { return m_seqCalls.Count; }
            }

            public override string ToString()
            {
                StringBuilder s = new StringBuilder("[");
                foreach (var call in m_seqCalls)
                {
                    s.Append(String.Format("{0},", call.m_id));
                }

                s.Remove(s.Length - 1, 1);
                s.Append(']');

                return s.ToString();
            }
        }

        List<CallDelta> BuildDeltas(List<ServiceCallItem> calls)
        {
            List<CallDelta> deltas = new List<CallDelta>();

            // Generate a list of deltas between all possible pairs of calls
            for (int i = 0; i < calls.Count(); ++i)
            {
                for (int j = i + 1; j < calls.Count(); ++j)
                {
                    var delta = new CallDelta { m_first = calls[i], m_second = calls[j] };

                    // If the delta is below the max freuquency then add it to the list
                    if (delta.DeltaAsMs <= m_maxFrequencyMs)
                    {
                        deltas.Add(delta);
                    }
                }
            }
            return deltas;
        }
        List<Sequence> GenerateSequences(List<CallDelta> deltas, double tolerancePercentage)
        {
            // Tolerance is how much we allow the delta to vary to call it a poll sequence
            double upperPercent = 1 + tolerancePercentage;
            double lowerPercent = 1 - tolerancePercentage;

            // For eaiser look up make a dicionary of delta grouped on the id of the first call in the pair
            var grouped = deltas.GroupBy(d => d.m_first.m_id).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var delta in deltas)
            {
                if (grouped.ContainsKey(delta.m_second.m_id))
                {
                    // Look through all of the deltas that could potentially be the enxt one in the sequence
                    foreach (var potential in grouped[delta.m_second.m_id].Where(d => d.m_previous == null))
                    {
                        // If the delta fits within the range, then hook the 2 deltas together
                        if (potential.Delta >= delta.Delta * lowerPercent && potential.Delta <= delta.Delta * upperPercent)
                        {
                            delta.m_next = potential;
                            potential.m_previous = delta;
                        }
                    }
                }
            }

            // Walk though all of the deltas that are the first in the sequence and build them up.
            List<Sequence> completed = new List<Sequence>();
            foreach (var delta in deltas.Where(d => d.m_previous == null))
            {
                completed.Add(Sequence.Create(delta));
            }

            // Remove the sequences that are subsets of other sets or are shorter that minimum size
            return RemoveSubsets(completed.Where(s => s.Length >= m_minSequenceSize).ToList());
        }
        static List<Sequence> RemoveSubsets(List<Sequence> sequences)
        {
            var finalSequences = new List<Sequence>();
            sequences = sequences.OrderBy(seq => seq.Length).ToList();

            // Start with the shortest sequences and see if they are contained in the longest sequences
            for (int i = 0; i < sequences.Count; ++i)
            {
                bool contained = false;
                for (int j = sequences.Count - 1; j > i; --j)
                {
                    // Because these are all sorted by length, once this reaches a sequence with the same length,
                    // it can stop looking and move on to the next sequence
                    if (sequences[j].Length == sequences[i].Length)
                    {
                        break;
                    }
                    else if (sequences[j].Contains(sequences[i]))
                    {
                        contained = true;
                        break;
                    }
                }
                if (contained == false)
                {
                    finalSequences.Add(sequences[i]);
                }
            }

            return finalSequences;
        }

        public override RuleResult Run(IEnumerable<ServiceCallItem> items, ServiceCallStats stats)
        {
            RuleResult result = InitializeResult(DisplayName, Description);
            if (items.Count() == 0) { 
                return result; 
            }

            //check invalid log versions
            if (items.Count(item => item.m_logVersion == Constants.Version1509) > 0)
            {
                result.AddViolation(ViolationLevel.Warning, "Data version does not support this rule. You need an updated Xbox Live SDK to support this rule");
                return result;
            }

            List<ServiceCallItem> allRepeats = new List<ServiceCallItem>();
            var nonShoulderTapItems = items.Where(item => item.m_isShoulderTap == false);

            List<Sequence> sequences = new List<Sequence>();
            foreach (ServiceCallItem thisItem in nonShoulderTapItems)
            {
                if (thisItem.m_reqHeader.Contains("SocialManager"))
                {
                    continue;
                }
                
                // Used to skip over already analyzed repeated calls.
                if (allRepeats.Contains(thisItem)) { 
                    continue; 
                }

                // Discover all repeats to thisItem
                var myRepeats = from item in nonShoulderTapItems
                                where ((item != thisItem) && (item.m_reqBodyHash == thisItem.m_reqBodyHash) && (item.m_uri == thisItem.m_uri))
                                select item;

                if (myRepeats.Count() > 1 )
                {
                    var deltas = BuildDeltas(myRepeats.ToList());
                    sequences.AddRange(GenerateSequences(deltas, m_sameDeltaThresholdPercentage));
                    
                    allRepeats.AddRange(myRepeats); 
                }
            }

            if (sequences == null)
            {
                return result;
            }

            //Process Violations
            StringBuilder description = new StringBuilder();
            foreach (var sequence in sequences.OrderByDescending(s => s.Length))
            {
                description.Clear();

                description.AppendFormat("Polling detected in a sequence of {0} calls at a rate of approximately {1} min:sec",
                                         sequence.Length, TimeSpan.FromTicks((long)sequence.m_averageDelta).ToString(@"mm\:ss\.fff"));

                result.AddViolation(ViolationLevel.Warning, description.ToString(), sequence.m_seqCalls);
            }

             
            result.Results.Add("Polling Sequences Found", result.ViolationCount);

            return result;
        }

        public override JObject SerializeJson()
        {
            var json = new JObject();
            json["SameDeltaThresholdPercent"] = m_sameDeltaThresholdPercentage;
            json["MaxFrequencyMs"] = m_maxFrequencyMs;
            json["MinSequenceSize"] = m_minSequenceSize;
            return json;
        }

        public override void DeserializeJson(JObject json)
        {
            Utils.SafeAssign(ref m_sameDeltaThresholdPercentage, json["SameDeltaThresholdPercent"]);
            Utils.SafeAssign(ref m_maxFrequencyMs, json["MaxFrequencyMs"]);
            Utils.SafeAssign(ref m_minSequenceSize, json["MinSequenceSize"]);
        }

        public static String DisplayName
        {
            get { return "Polling Detection"; }
        }

        public static String Description
        {
            get { return "Finds sequences of calls that appear to be polling of a service."; }
        }
    }
}



