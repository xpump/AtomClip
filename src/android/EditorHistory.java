package dev.atomclip.mobile;

import java.util.ArrayDeque;

/** In-memory, per-route editing transactions. Never written to logs or preferences. */
final class EditorHistory {
 static final class Snapshot {
  final String value; final int start,end;
  Snapshot(String value,int start,int end){this.value=value;this.start=start;this.end=end;}
 }
 final ArrayDeque<Snapshot> undo=new ArrayDeque<>();
 String value=""; int start,end; long lastEdit=-2000; int lastChangeEnd=-1; boolean grouped;
 void record(String before,int a,int b,int offset,int removed,int added,long now,boolean separate){
  boolean contiguous=grouped&&!separate&&now-lastEdit<800&&(offset==lastChangeEnd||offset+removed==lastChangeEnd)&&Math.max(removed,added)<=2;
  if(!contiguous){undo.addLast(new Snapshot(before,a,b));int characters=0;for(Snapshot item:undo)characters+=item.value.length();while(undo.size()>1&&(undo.size()>40||characters>1024*1024))characters-=undo.removeFirst().value.length();}
  lastChangeEnd=offset+added;lastEdit=now;grouped=!separate;
 }
 void boundary(){grouped=false;}
 boolean canUndo(){return !undo.isEmpty();}
 Snapshot undo(){boundary();return undo.pollLast();}
 void remember(String v,int a,int b){value=v;start=Math.max(0,a);end=Math.max(0,b);}
}
