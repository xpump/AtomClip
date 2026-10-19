package dev.atomclip.mobile;
import java.util.*;

/** Display-only merge. Reading Info never creates/selects a binding or bypasses authentication. */
final class BoardPresence {
 static class Observation {String address="",shortId="";int profile,rssi;V2Protocol.Info info;}
 static final class Row {
  V2Protocol.Binding binding;Observation nearby;boolean conflict;
  String number(){return binding!=null?binding.info.number():nearby.info==null?"":nearby.info.number();}
 }
 static List<Row> merge(Collection<V2Protocol.Binding> bindings,Collection<? extends Observation> nearby){
  LinkedHashMap<String,Observation> unique=new LinkedHashMap<>();for(Observation s:nearby)unique.put(s.address.toUpperCase(Locale.ROOT),s);
  ArrayList<Observation> signals=new ArrayList<>(unique.values());ArrayList<Row> rows=new ArrayList<>();HashSet<Observation> used=new HashSet<>();
  for(V2Protocol.Binding b:bindings){ArrayList<Observation> matches=new ArrayList<>();for(Observation s:signals)if(s.info!=null&&s.info.number().equals(b.info.number()))matches.add(s);Row r=new Row();r.binding=b;r.conflict=matches.size()>1;r.nearby=matches.size()==1?matches.get(0):null;rows.add(r);if(r.nearby!=null)used.add(r.nearby);}
  for(Observation s:signals)if(!used.contains(s)){Row r=new Row();r.nearby=s;if(s.info!=null){int count=0;for(Observation t:signals)if(t.info!=null&&t.info.number().equals(s.info.number()))count++;r.conflict=count>1;}rows.add(r);}return rows;
 }
}
