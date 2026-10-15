using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// The actual generated room/node/connectivity manifest travels with world start.
// Seed generation supplies the original meshes; a differing topology is rejected before play.
public static class CoopTopology
{
    public static System.Collections.Generic.List<RoomData> Rooms(Map map) => map.rooms.Concat(map.extendedRooms).Distinct().ToList();
    private static JArray Vector(Vector3 v) => new JArray(Math.Round(v.x, 3), Math.Round(v.y, 3), Math.Round(v.z, 3));
    public static string Capture(Map map)
    {
        var result = new JArray();
        var orderedRooms = Rooms(map);
        foreach (RoomData room in orderedRooms)
        {
            var nodes = new JArray();
            foreach (PathNode node in room.pathNodes)
                nodes.Add(new JObject { ["centre"] = Vector(node.centrePoint),
                    ["area"] = new JArray(Math.Round(node.area.x, 3), Math.Round(node.area.y, 3), Math.Round(node.area.width, 3), Math.Round(node.area.height, 3)),
                    ["links"] = new JArray(node.pathConnections.Select(c => new JArray(orderedRooms.IndexOf(c.connectsTo.associatedRoom), c.connectsTo.associatedRoom.pathNodes.IndexOf(c.connectsTo)))) });
            result.Add(new JObject { ["kind"] = room.gameObject.name, ["floor"] = room.floor,
                ["position"] = Vector(room.transform.position), ["nodes"] = nodes });
        }
        return result.ToString(Formatting.None);
    }
    public static string Digest(string manifest)
    {
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(manifest))).Replace("-", "");
    }
    public static void Validate(Map map, string authoritative)
    {
        if (string.IsNullOrEmpty(authoritative) || authoritative.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Missing/oversized room topology");
        string local = Capture(map);
        // Post-generation portals are randomized by the original game after resetting its seed.
        // Their actual links are applied through the entity snapshot, with portal references.
        var localRooms = JArray.Parse(local);
        var hostRooms = JArray.Parse(authoritative);
        foreach (var rooms in new[] { localRooms, hostRooms })
            foreach (JObject room in rooms)
                foreach (JObject node in (JArray)room["nodes"]) node.Remove("links");
        if (localRooms.Count != hostRooms.Count) throw new InvalidOperationException("Room manifest size differs from host");
        var ordered = Rooms(map);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (JToken.DeepEquals(localRooms[i], hostRooms[i])) continue;
            // The original UFO is positioned AFTER the time-based reseed. It must use the
            // host's geometry and nodes, while ordinary seed-generated rooms must match.
            if (!(ordered[i] is UFOMover))
                throw new InvalidOperationException("Geometry differs at room " + i + "; host=" + hostRooms[i] + "; local=" + localRooms[i]);
            var room = ordered[i];
            JToken p = hostRooms[i]["position"];
            Vector3 target = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            Vector3 delta = target - room.transform.position;
            room.transform.position = target;
            room.area = new Rect(room.area.x + delta.x, room.area.y + delta.z, room.area.width, room.area.height);
            room.centrePosition += delta;
            for (int j = 0; j < room.pathNodes.Count; j++)
            {
                JToken n = hostRooms[i]["nodes"][j], a = n["area"], centre = n["centre"];
                room.pathNodes[j].area = new Rect((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                room.pathNodes[j].centrePoint = new Vector3((float)centre[0], (float)centre[1], (float)centre[2]);
            }
        }
        Debug.Log("[Coop] Actual topology verified; rooms=" + map.rooms.Count + "; hash=" + Digest(local));
    }
}
