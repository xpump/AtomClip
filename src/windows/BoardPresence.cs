using System;
using System.Collections.Generic;
using System.Linq;

namespace AtomClipUsb
{
    // Discovery merges presentation only. Info reads never create/select bindings or authenticate a peer.
    public static class BoardPresence
    {
        public class Observation
        {
            public string Address = "", ShortId = "";
            public int Profile, Rssi;
            public V2Protocol.Info Info;
        }
        public sealed class Row
        {
            public V2Protocol.Binding Binding;
            public Observation Nearby;
            public bool Conflict;
            public string Number { get { return Binding != null ? Binding.DeviceInfo.Number : Nearby.Info == null ? "" : Nearby.Info.Number; } }
        }
        public static List<Row> Merge(IEnumerable<V2Protocol.Binding> bindings, IEnumerable<Observation> nearby)
        {
            var signals = nearby.GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()).ToList();
            var rows = new List<Row>(); var used = new HashSet<Observation>();
            foreach (var b in bindings.OrderBy(x => x.DeviceInfo.Number))
            {
                var matches = signals.Where(x => x.Info != null && x.Info.Number == b.DeviceInfo.Number).ToList();
                var row = new Row { Binding = b, Conflict = matches.Count > 1, Nearby = matches.Count == 1 ? matches[0] : null };
                rows.Add(row); if (row.Nearby != null) used.Add(row.Nearby);
            }
            foreach (var s in signals.Where(x => !used.Contains(x))) rows.Add(new Row { Nearby = s, Conflict = s.Info != null && signals.Count(x => x.Info != null && x.Info.Number == s.Info.Number) > 1 });
            return rows;
        }
    }
}
