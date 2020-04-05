using System;
using ParallelTasks;
using Sandbox.ModAPI;
using VRage.Game;

namespace Digi.ProjectorPreview
{
    static class BlueprintThread
    {
        public class Data : WorkData
        {
            public string SerializedBlueprint = null;
            public MyObjectBuilder_CubeGrid Blueprint = null;
            public readonly bool Serialize;

            /// <summary>
            /// Used for serialization
            /// </summary>
            public Data(MyObjectBuilder_CubeGrid blueprint)
            {
                Serialize = true;
                Blueprint = blueprint;
            }

            /// <summary>
            /// Used for deserialization.
            /// </summary>
            public Data(string serialized)
            {
                Serialize = false;
                SerializedBlueprint = serialized;
            }
        }

        public static Action<WorkData> Run = new Action<WorkData>(RunMethod);
        private static void RunMethod(WorkData workData)
        {
            var data = (Data)workData;

            if(data.Serialize)
            {
                data.SerializedBlueprint = MyAPIGateway.Utilities.SerializeToXML(data.Blueprint);
            }
            else // Deserialize
            {
                data.Blueprint = MyAPIGateway.Utilities.SerializeFromXML<MyObjectBuilder_CubeGrid>(data.SerializedBlueprint);
            }
        }
    }
}
