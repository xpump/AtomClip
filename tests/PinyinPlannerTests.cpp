#include "../src/firmware/AtomClip/PinyinInput.h"
constexpr uint8_t mixed[]={0xe4,0xb8,0xad,0xe6,0x96,0x87,'A','b','1','2','3',0xef,0xbc,0x8c,0xe3,0x80,0x82};
constexpr bool correctPlan() {
  char trace[80]={};size_t n=0;
  auto direct=[&](uint8_t c){trace[n++]=char(c);return true;};
  auto unicode=[&](uint16_t c){char keys[8]={};pinyin::vuc(c,keys);for(unsigned i=0;i<7;i++)trace[n++]=keys[i];trace[n++]=' ';return true;};
  auto toggle=[&](bool english){trace[n++]=english?'+':'-';return true;};
  if(!pinyin::plan(mixed,sizeof(mixed),direct,unicode,toggle))return false;
  const char expected[]="vuc4e2d vuc6587 +Ab123-vucff0c vuc3002 ";
  if(n!=sizeof(expected)-1)return false;
  for(size_t i=0;i<n;i++)if(trace[i]!=expected[i])return false;
  return true;
}
constexpr bool restoresAfterAscii() {
  const uint8_t text[]={'A','B','\n','\t'};char modes[3]={};int n=0;
  return pinyin::plan(text,sizeof(text),[](uint8_t){return true;},[](uint16_t){return true;},[&](bool en){modes[n++]=en?'+':'-';return true;}) && n==2 && modes[0]=='+' && modes[1]=='-';
}
constexpr bool cancellationStops() {
  unsigned emitted=0;
  bool ok=pinyin::plan(mixed,sizeof(mixed),[&](uint8_t){emitted++;return true;},[&](uint16_t){emitted++;return false;},[&](bool){emitted++;return true;});
  return !ok && emitted==1;
}
constexpr bool mixedProbe() {
  const uint8_t probe[]={0xe7,0x97,0x85,'A','B','C','1','2','3',0xef,0xbc,0x8c,0xe4,0xb8,0xad,0xe6,0x96,0x87,'!'};
  char trace[80]={};size_t n=0;
  bool ok=pinyin::plan(probe,sizeof(probe),[&](uint8_t c){trace[n++]=char(c);return true;},[&](uint16_t c){char s[8]={};pinyin::vuc(c,s);for(unsigned i=0;i<7;i++)trace[n++]=s[i];trace[n++]=' ';return true;},[&](bool english){trace[n++]=english?'+':'-';return true;});
  const char expected[]="vuc75c5 +ABC123-vucff0c vuc4e2d vuc6587 +!-";
  if(!ok||n!=sizeof(expected)-1)return false;
  for(size_t i=0;i<n;i++)if(trace[i]!=expected[i])return false;
  return true;
}
constexpr uint8_t emoji[]={0xf0,0x9f,0x98,0x80},overlong[]={0xe0,0x80,0xaf},surrogate[]={0xed,0xa0,0x80},truncated[]={0xe4,0xb8},nul[]={0},badControl[]={0x7f},badEnd[]={0xef,0xbf,0xbf};
static_assert(correctPlan(),"exact mixed Chinese/ASCII code points and minimal mode switches");
static_assert(restoresAfterAscii(),"English runs preserve raw newline/tab and restore Chinese state");
static_assert(cancellationStops(),"cancel immediately without further toggle or typing");
static_assert(mixedProbe(),"the exact Chinese/English/digit/punctuation trial restores Chinese after trailing ASCII");
static_assert(!pinyin::valid(emoji,sizeof(emoji))&&!pinyin::valid(overlong,sizeof(overlong))&&!pinyin::valid(surrogate,sizeof(surrogate))&&!pinyin::valid(truncated,sizeof(truncated)),"reject non-BMP and malformed UTF8");
static_assert(!pinyin::valid(nul,sizeof(nul))&&!pinyin::valid(badControl,sizeof(badControl))&&!pinyin::valid(badEnd,sizeof(badEnd)),"reject control and non-character data");
