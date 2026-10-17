#include "../src/firmware/AtomClip/GbkInput.h"
constexpr uint8_t mixed[]={0xb2,0xa1,'A','B','C','1','2','3',0xa3,0xac,0xd6,0xd0,0xce,0xc4,'!','\n','\t'};
constexpr bool mixedPlan() {
  uint16_t output[20]={};size_t n=0;
  bool result=gbk::plan(mixed,sizeof(mixed),[&](uint8_t c){output[n++]=c;return true;},[&](uint16_t c){output[n++]=c;return true;},[](){return true;});
  const uint16_t expected[]={45729,'A','B','C','1','2','3',41900,54992,52932,'!','\n','\t'};
  if(!result || n!=sizeof(expected)/2)return false;
  for(size_t i=0;i<n;i++)if(output[i]!=expected[i])return false;
  return true;
}
constexpr bool atomicDigits() {
  uint8_t keys[12]={};bool downs[12]={};unsigned n=0,total=0;
  bool result=gbk::emit(45729,2,2,3,6,[&](uint8_t key,bool down){keys[n]=key;downs[n++]=down;return true;},[&](uint8_t ms){total+=ms;});
  const uint8_t expected[]={0xe2,0x5c,0x5c,0x5d,0x5d,0x5f,0x5f,0x5a,0x5a,0x61,0x61,0xe2};
  if(!result || n!=12 || total!=29)return false;
  for(unsigned i=0;i<12;i++)if(keys[i]!=expected[i] || downs[i]!=(i==0 || (i%2==1 && i!=11)))return false;
  return true;
}
constexpr bool stopBoundary() {
  unsigned count=0;
  return !gbk::plan(mixed,sizeof(mixed),[&](uint8_t){count++;return true;},[&](uint16_t){count++;return true;},[&](){return count<1;}) && count==1;
}
constexpr bool stopWhileAltIsHeld() {
  bool stop=false;unsigned strokes=0,characters=0;
  bool result=gbk::plan(mixed,sizeof(mixed),[](uint8_t){return false;},[&](uint16_t code){characters++;return gbk::emit(code,2,2,3,6,[&](uint8_t,bool){strokes++;return true;},[&](uint8_t){stop=true;});},[&](){return !stop;});
  return !result && stop && characters==1 && strokes==12;
}
constexpr bool noPartialInvalidInput() {
  const uint8_t bad[]={'A',0xb2};unsigned strokes=0;
  return !gbk::plan(bad,2,[&](uint8_t){strokes++;return true;},[&](uint16_t){strokes++;return true;},[](){return true;}) && strokes==0;
}
constexpr uint8_t truncated[]={0xb2},badTrail[]={0xb2,0x7f},singleEuro[]={0x80},control[]={0x1b};
static_assert(mixedPlan(),"Chinese GBK values and literal English/digits/punctuation/newline/tab, in order");
static_assert(atomicDigits(),"45729 keeps Alt pressed across all five keypad digits, including releases");
static_assert(stopBoundary(),"stop between complete characters, without emitting later ASCII");
static_assert(stopWhileAltIsHeld(),"stop request finishes only the current five-digit code and releases Alt");
static_assert(noPartialInvalidInput(),"validate the entire stream before the first ASCII keystroke");
static_assert(!gbk::valid(truncated,1)&&!gbk::valid(badTrail,2)&&!gbk::valid(singleEuro,1)&&!gbk::valid(control,1),"reject malformed streams and shortcuts before sending anything");
