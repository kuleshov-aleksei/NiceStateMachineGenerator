﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NiceStateMachineGenerator
{
    public sealed class Parser
    {
        private static readonly JsonLoadSettings s_jsonLoadSettings = new JsonLoadSettings() {
            CommentHandling = CommentHandling.Ignore,
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            LineInfoHandling = LineInfoHandling.Load
        };

        public static StateMachineDescr Parse(JObject json)
        {
            Parser parser = new Parser();
            return parser.ParseInternal(json);
        }

        public static StateMachineDescr ParseFile(string fileName)
        {
            JObject json = JObject.Parse(
                File.ReadAllText(fileName),
                s_jsonLoadSettings
            );
            return Parse(json);
        }

        private readonly Dictionary<string, TimerDescr> m_timers = new Dictionary<string, TimerDescr>();
        private readonly Dictionary<string, EventDescr> m_events = new Dictionary<string, EventDescr>();
        private readonly Dictionary<string, StateDescr> m_states = new Dictionary<string, StateDescr>();

        private readonly HashSet<string> m_stateNames = new HashSet<string>();
        private readonly HashSet<string> m_eventNames = new HashSet<string>();
        private readonly HashSet<string> m_timerNames = new HashSet<string>();

        private static void AddRange<T>(HashSet<T> set, IEnumerable<T> values)
        {
            foreach (T t in values)
            {
                if (!set.Add(t))
                {
                    throw new ApplicationException("Duplicate value " + t);
                }
            }
        }

        private StateMachineDescr ParseInternal(JObject json)
        {
            HashSet<string> handledTokens = new HashSet<string>();

            JObject eventsObject = ParserHelper.GetJObjectRequired(json, "events", handledTokens);
            JObject statesObject = ParserHelper.GetJObjectRequired(json, "states", handledTokens);
            JObject? timersObject = ParserHelper.GetJObject(json, "timers", handledTokens, required: false);
            if (timersObject != null)
            {
                ParseTimers(timersObject);
            };

            AddRange(this.m_timerNames, this.m_timers.Keys);
            AddRange(this.m_stateNames, statesObject.Properties().Select(p => p.Name));

            ParseEvents(eventsObject);
            AddRange(this.m_eventNames, this.m_events.Keys);

            ParseStates(statesObject);

            string startStateName = ParserHelper.GetJStringRequired(json, "start_state", handledTokens, out JToken startStateToken);
            if (!this.m_stateNames.Contains(startStateName))
            {
                throw new ParseValidationException(startStateToken, $"Unknown start state name: '{startStateName}'");
            };

            ParserHelper.CheckAllTokensHandled(json, handledTokens);

            return new StateMachineDescr(
                startState: startStateName,
                timers: this.m_timers,
                events: this.m_events,
                states: this.m_states
            );
        }

        private void ParseStates(JObject statesObject)
        {
            foreach (JProperty property in statesObject.Properties())
            {
                StateDescr stateDescr = new StateDescr(property.Name);

                if (property.Value.Type != JTokenType.Object)
                {
                    throw new ParseValidationException(property.Value, $"State description should be an object");
                };
                ParseState(stateDescr, (JObject)property.Value);

                this.m_states.Add(stateDescr.Name, stateDescr);
            }
        }

        private void ParseStopTimersArray(HashSet<string> result, JObject json, string tokenName, HashSet<string> handledTokens)
        {
            JArray? timers = ParserHelper.GetJArray(json, tokenName, handledTokens, required: false);
            if (timers == null || timers.Count == 0)
            {
                return;
            }
            foreach (JToken timerToken in timers)
            {
                string timerName = ParserHelper.CheckAndConvertToString(timerToken, "Timer name");
                if (!this.m_timers.ContainsKey(timerName))
                {
                    throw new ParseValidationException(timerToken, $"Undeclared timer name in state {tokenName} description: '{timerName}'");
                };
                if (!result.Add(timerName))
                {
                    throw new ParseValidationException(timerToken, $"Duplicate timer name in state {tokenName} description: '{timerName}'");
                };
            }
        }

        private void ParseStartTimersArray(Dictionary<string, TimerStartDescr> result, JObject json, string tokenName, HashSet<string> handledTokens)
        {
            JArray? timers = ParserHelper.GetJArray(json, tokenName, handledTokens, required: false);
            if (timers == null || timers.Count == 0)
            {
                return;
            }
            foreach (JToken timerToken in timers)
            {
                TimerStartDescr descr;

                switch (timerToken.Type)
                {
                case JTokenType.String:
                    descr = new TimerStartDescr(ParserHelper.CheckAndConvertToString(timerToken, "Timer name"));
                    break;
                case JTokenType.Object:
                    descr = ParseTimerStartObject((JObject)timerToken);
                    break;
                default:
                    throw new ParseValidationException(timerToken, "Timer start token should be either String or Object");
                }
                
                if (!this.m_timers.ContainsKey(descr.TimerName))
                {
                    throw new ParseValidationException(timerToken, $"Undeclared timer name in state {tokenName} description: '{descr.TimerName}'");
                };
                if (!result.TryAdd(descr.TimerName, descr))
                {
                    throw new ParseValidationException(timerToken, $"Duplicate timer name in state {tokenName} description: '{descr.TimerName}'");
                };
            }
        }

        private TimerStartDescr ParseTimerStartObject(JObject timerObject)
        {
            HashSet<string> handledTokens = new HashSet<string>();

            string timerName = ParserHelper.GetJStringRequired(timerObject, "timer", handledTokens, out _);
            TimerStartDescr result = new TimerStartDescr(timerName);

            JToken? modifyToken = ParserHelper.GetJToken(timerObject, "modify", handledTokens, required: false);
            if (modifyToken != null)
            {
                switch (modifyToken.Type)
                {
                case JTokenType.Integer:
                    result.Modify = new TimerModifyDescr() {
                        set = (int)modifyToken
                    };
                    break;
                case JTokenType.Float:
                    result.Modify = new TimerModifyDescr() {
                        set = (double)modifyToken
                    };
                    break;
                case JTokenType.Object:
                    result.Modify = ParseTimerModifyObject((JObject)modifyToken);
                    break;
                default:
                    throw new ParseValidationException(modifyToken, "Timer modify token should be either a numeric value or object");
                }
                
            }

            ParserHelper.CheckAllTokensHandled(timerObject, handledTokens);

            return result;
        }

        private TimerModifyDescr ParseTimerModifyObject(JObject modifyObject)
        {
            TimerModifyDescr result = new TimerModifyDescr();

            HashSet<string> handledTokens = new HashSet<string>();

            result.increment = ParserHelper.GetJDouble(modifyObject, "increment", handledTokens, required: false);
            result.multiplier = ParserHelper.GetJDouble(modifyObject, "multiplier", handledTokens, required: false);
            result.min = ParserHelper.GetJDouble(modifyObject, "min", handledTokens, required: false);
            result.max = ParserHelper.GetJDouble(modifyObject, "max", handledTokens, required: false);

            ParserHelper.CheckAllTokensHandled(modifyObject, handledTokens);

            if (result.increment == null 
                && result.multiplier == null
            )
            {
                throw new ParseValidationException(modifyObject, "Timer modify object shouyld contain either 'increment' or 'multiplier' value");
            }

            return result;
        }

        private void ParseState(StateDescr stateDescr, JObject json)
        {
            HashSet<string> handledTokens = new HashSet<string>();

            ParseStartTimersArray(stateDescr.StartTimers, json, "start_timers", handledTokens);
            ParseStopTimersArray(stateDescr.StopTimers, json, "stop_timers", handledTokens);

            JToken? onEnterToken = ParserHelper.GetJToken(json, "on_enter", handledTokens, required: false);
            if (onEnterToken == null)
            {
                stateDescr.NeedOnEnterEvent = false;
            }
            else
            {
                switch (onEnterToken.Type)
                {
                case JTokenType.Boolean:
                    stateDescr.NeedOnEnterEvent = (bool)onEnterToken;
                    break;
                case JTokenType.Object:
                    stateDescr.NeedOnEnterEvent = true;
                    stateDescr.OnEnterEventAlluxTargets = ParseSubEdges((JObject)onEnterToken);
                    break;
                default:
                    throw new ParseValidationException(onEnterToken, $"'on_enter' should be either Boolean or Object, got {onEnterToken.Type} ({onEnterToken.ToString(Newtonsoft.Json.Formatting.None)})");
                }
            }
            stateDescr.OnEnterEventComment = ParserHelper.GetJString(json, "on_enter_comment", handledTokens, out JToken? commentToken, required: false);

            if (stateDescr.OnEnterEventComment != null && !stateDescr.NeedOnEnterEvent)
            {
                throw new ParseValidationException(commentToken, "On enter comment is specified, but no event required");
            }

            stateDescr.TimerEdges = ParseEdges(json, "on_timer", handledTokens, this.m_timerNames, stateDescr.Name, isTimer: true);
            stateDescr.EventEdges = ParseEdges(json, "on_event", handledTokens, this.m_eventNames, stateDescr.Name, isTimer: false);

            string? nextStateName = ParserHelper.GetJString(json, "next_state", handledTokens, out JToken? nextStateToken, required : false);
            if (nextStateName != null)
            {
                stateDescr!.NextStateName = nextStateName;
                if (!this.m_stateNames.Contains(stateDescr.NextStateName))
                {
                    throw new ParseValidationException(nextStateToken, $"Unknown next state name '{stateDescr.NextStateName}'");
                }
            }

            stateDescr!.IsFinal = ParserHelper.GetJBoolWithDefault(json, "final", false, handledTokens);
            stateDescr.Color = ParserHelper.GetJString(json, "color", handledTokens, out JToken? _, required: false);

            //sanity check
            {
                int typeTokensCount = 0;
                if (stateDescr.TimerEdges != null || stateDescr.EventEdges != null)
                {
                    ++typeTokensCount;
                };

                if (stateDescr.NextStateName != null)
                {
                    ++typeTokensCount;
                };

                if (stateDescr.IsFinal)
                {
                    ++typeTokensCount;
                };

                if (typeTokensCount == 0)
                {
                    throw new ParseValidationException(json, $"Failed to determine state type for state '{stateDescr.Name}'. Either 'on_timer'/'on_event' or 'next_state' or 'final' should be specified");
                }
                else if (typeTokensCount != 1)
                {
                    throw new ParseValidationException(json, $"Conflicting states type for state '{stateDescr.Name}'. Either 'on_timer'/'on_event' or 'next_state' or 'final' could be specified");
                };
            };

            ParserHelper.CheckAllTokensHandled(json, handledTokens);
        }

        private Dictionary<string, EdgeDescr>? ParseEdges(JObject json, string tokenName, HashSet<string> handledTokens, HashSet<string> knownNames, string sourceStateName,  bool isTimer)
        {
            JObject? container = ParserHelper.GetJObject(json, tokenName, handledTokens, required: false);
            if (container == null)
            {
                return null;
            };
            Dictionary<string, EdgeDescr> edges = new Dictionary<string, EdgeDescr>();
            foreach (JProperty property in container.Properties())
            {
                if (!knownNames.Contains(property.Name))
                {
                    throw new ParseValidationException(property, $"Unexpected edge name '{property.Name}' in {tokenName}");
                };
                if (edges.ContainsKey(property.Name))
                {
                    throw new ParseValidationException(property, $"Duplicated edge name '{property.Name}' in {tokenName}");
                };

                EdgeDescr edge = new EdgeDescr(property.Name, isTimer: isTimer);
                switch (property.Value.Type)
                {
                case JTokenType.Null:
                case JTokenType.String:
                case JTokenType.Boolean:
                    edge.Target = ParseEdgeTarget(property.Value);
                    break;
                case JTokenType.Object:
                    ParseEdgeDetailed(edge, (JObject)property.Value, sourceStateName);
                    break;
                default:
                    throw new ParseValidationException(property.Value, "Unexpected edge type");
                };
                
                edges.Add(edge.InvokerName, edge);
            };
            return edges;
        }

        private EdgeTarget ParseEdgeTarget(JToken token)
        {
            switch (token.Type)
            {
            case JTokenType.Null:
                return EdgeTarget.CreateNoChangeTarget();
            case JTokenType.String:
                string stateName = ParserHelper.CheckAndConvertToString(token, "edge target");
                if (!this.m_stateNames.Contains(stateName))
                {
                    throw new ParseValidationException(token, $"Unknown target state name '{stateName}'");
                };
                return EdgeTarget.CreateStateTarget(stateName);
            case JTokenType.Boolean:
                {
                    bool value = (bool)token;
                    if (value != false)
                    {
                        throw new ParseValidationException(token, "Only 'false' could be specified as a failure edge value");
                    };
                    return EdgeTarget.CreateFailureTarget();
                }
            default:
                throw new ParseValidationException(token, $"Unexpected edge target type: {token.Type}. Only Null, String or Boolean are allowed");
            }
        }

        private void ParseEdgeDetailed(EdgeDescr edge, JObject description, string sourceStateName)
        {
            HashSet<string> handledTokens = new HashSet<string>();

            string? targetStateName = ParserHelper.GetJString(description, "state", handledTokens, out JToken? targetStateNameToken, false);
            JObject? targetStates = ParserHelper.GetJObject(description, "states", handledTokens, false);
            if (targetStateName != null && targetStates != null)
            {
                throw new ParseValidationException(description, "Only one of 'state' and 'states' properties mey be scpecified for an edge.");
            }
            else if (targetStateName == null && targetStates == null)
            {
                edge.Target = EdgeTarget.CreateNoChangeTarget();
            }
            else if (targetStateName != null)
            {
                if (!this.m_stateNames.Contains(targetStateName))
                {
                    throw new ParseValidationException(targetStateNameToken, $"Unknown target state name '{targetStateName}'");
                };
                edge.Target = EdgeTarget.CreateStateTarget(targetStateName);
            }
            else if (targetStates != null)
            {
                if (targetStates.Count == 0)
                {
                    throw new ParseValidationException(targetStates, "Empty 'states' object.");
                };
                edge.Targets = ParseSubEdges(targetStates);
            }

            JToken? traverseEventsToken = ParserHelper.GetJToken(description, "on_traverse", handledTokens, required: false);
            ParseOnTraverseEvents(edge.OnTraverseEventTypes, traverseEventsToken);

            if (edge.Targets != null)
            {
                if (edge.OnTraverseEventTypes.Count == 0)
                {
                    throw new ParseValidationException(targetStates, "Sub-edges mapping ('states') is only available as a result of on_traverse event, but it is not requested");
                }
                else if (edge.OnTraverseEventTypes.Count > 1)
                {
                    throw new ParseValidationException(targetStates, $"For an edge with sub-edges mapping ('states') only single on_traverse event type may be specified");
                }
                else if (
                    edge.OnTraverseEventTypes.Contains(EdgeTraverseCallbackType.full)
                    || edge.OnTraverseEventTypes.Contains(EdgeTraverseCallbackType.event_and_target)
                    || edge.OnTraverseEventTypes.Contains(EdgeTraverseCallbackType.source_and_target)
                    || edge.OnTraverseEventTypes.Contains(EdgeTraverseCallbackType.target_only)
                )
                {
                    throw new ParseValidationException(targetStates, $"For an edge with sub-edges mapping ('states') on_traverse event type can not include target state (types '{EdgeTraverseCallbackType.event_and_target}', '{EdgeTraverseCallbackType.source_and_target}', '{EdgeTraverseCallbackType.target_only}' and '{EdgeTraverseCallbackType.full}' are forbiden)");
                }
            };

            edge.TraverseEventComment = ParserHelper.GetJString(description, "on_traverse_comment", handledTokens, out JToken? commentToken, required: false);
            if (edge.TraverseEventComment != null && edge.OnTraverseEventTypes.Count == 0)
            {
                throw new ParseValidationException(commentToken, "On traverse comment is specified, but no event required");
            };

            edge.Color = ParserHelper.GetJString(description, "color", handledTokens, out JToken? _, required: false);

            ParserHelper.CheckAllTokensHandled(description, handledTokens);
        }

        private Dictionary<string, EdgeTarget> ParseSubEdges(JObject targetStates)
        {
            Dictionary<string, EdgeTarget> subEdges = new Dictionary<string, EdgeTarget>();
            HashSet<EdgeTarget> targets = new HashSet<EdgeTarget>();
            foreach (JProperty prop in targetStates.Properties())
            {
                string comment = prop.Name;
                EdgeTarget target = ParseEdgeTarget(prop.Value);
                if (!targets.Add(target))
                {
                    throw new ParseValidationException(prop.Value, $"Duplicated sub-edge target specified: {target}");
                }
                else if (subEdges.ContainsKey(comment))
                {
                    throw new ParseValidationException(prop, $"Duplicated sub-edge comment '{comment}'. Please choose distinct comments for sub-edges");
                }
                else if (target.TargetType == EdgeTargetType.failure)
                {
                    throw new ParseValidationException(prop, $"There's no meaning in specifying failure ('false') for sub-edge.");
                };
                subEdges.Add(comment, target);
            };
            return subEdges;
        }

        private void ParseOnTraverseEvents(HashSet<EdgeTraverseCallbackType> onTraverseEventTypes, JToken? traverseEventsToken)
        {
            if (traverseEventsToken == null)
            {
                return;
            }
            switch (traverseEventsToken.Type)
            {
            case JTokenType.Null:
                return;
            case JTokenType.String:
                ParseOnTraverseEventSingleValue(onTraverseEventTypes, traverseEventsToken);
                return;
            case JTokenType.Array:
                {
                    JArray array = (JArray)traverseEventsToken;
                    foreach (JToken element in array)
                    {
                        ParseOnTraverseEventSingleValue(onTraverseEventTypes, element);
                    };
                }
                return;
            default:
                throw new ParseValidationException(traverseEventsToken, "On_traverse should be either String or string Array, or Null. Specified: " + traverseEventsToken.Type);
            }
        }

        private void ParseOnTraverseEventSingleValue(HashSet<EdgeTraverseCallbackType> onTraverseEventTypes, JToken element)
        {
            string eventType = ParserHelper.CheckAndConvertToString(element, "event type");
            if (!Enum.TryParse(eventType, out EdgeTraverseCallbackType value))
            {
                throw new ParseValidationException(element, $"Failed to convert value '{eventType}' to on_traverse type. Possible values are {String.Join(", ", Enum.GetNames<EdgeTraverseCallbackType>())}");
            };
            if (!onTraverseEventTypes.Add(value))
            {
                throw new ParseValidationException(element, $"Duplicate on_traverse value '{eventType}'");
            };
        }

        private void ParseEvents(JObject eventsObject)
        {
            foreach (JProperty property in eventsObject.Properties())
            {
                EventDescr eventDescr = new EventDescr(property.Name);

                if (property.Value.Type != JTokenType.Object)
                {
                    throw new ParseValidationException(property.Value, $"Event description should be an object");
                };
                ParseEvent(eventDescr, (JObject)property.Value);

                this.m_events.Add(eventDescr.Name, eventDescr);
            }
        }

        private void ParseEvent(EventDescr eventDescr, JObject json)
        {
            HashSet<string> handledTokens = new HashSet<string>();

            //after states
            {
                JArray? afterStates = ParserHelper.GetJArray(json, "after_states", handledTokens, required: false);
                if (afterStates != null && afterStates.Count > 0)
                {
                    eventDescr.AfterStates = new HashSet<string>();
                    foreach (JToken stateToken in afterStates)
                    {
                        string stateName = ParserHelper.CheckAndConvertToString(stateToken, "state name");
                        if (!this.m_stateNames.Contains(stateName))
                        {
                            throw new ParseValidationException(stateToken, $"Undeclared state name in event after_states description: '{stateName}'");
                        };
                        if (!eventDescr.AfterStates.Add(stateName))
                        {
                            throw new ParseValidationException(stateToken, $"Duplicate state name in event after_states description: '{stateName}'");
                        };
                    }
                };
            };

            {
                JObject? args = ParserHelper.GetJObject(json, "args", handledTokens, required: false);
                if (args != null && args.Count > 0)
                {
                    foreach (JProperty property in args.Properties())
                    {
                        string argName = property.Name;
                        if (eventDescr.Args.Any(p => p.Key == argName))
                        {
                            throw new ParseValidationException(property, $"duplicate arg name '{argName}'");
                        };
                        string argType = ParserHelper.CheckAndConvertToString(property.Value, "arg value");
                        eventDescr.Args.Add(new KeyValuePair<string, string>(argName, argType));
                    }
                }
            };

            eventDescr.OnlyOnce = ParserHelper.GetJBoolWithDefault(json, "only_once", false, handledTokens);

            ParserHelper.CheckAllTokensHandled(json, handledTokens);
        }

        private void ParseTimers(JObject timersObject)
        {
            foreach (JProperty prop in timersObject.Properties())
            {
                double timeout;
                if (prop.Value.Type == JTokenType.Integer)
                {
                    timeout = (long)prop.Value;
                }
                else if (prop.Value.Type == JTokenType.Float)
                {
                    timeout = (double)prop.Value;
                }
                else
                {
                    throw new ParseValidationException(prop.Value, "Bad timer description type: " + prop.Value.Type);
                };
                TimerDescr timer = new TimerDescr(prop.Name, timeout);
                this.m_timers.Add(timer.Name, timer);
            }
        }



    }
}
