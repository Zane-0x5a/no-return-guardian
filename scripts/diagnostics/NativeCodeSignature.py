"""What a game build must share with a known build before the native scripts touch it.

The native scripts hook, call, compare return addresses with, and read game state at fixed addresses, each
named by where it lies in one build of the game: the verified build. Instead of accepting only that exact
executable, a build is accepted when it is one of the known builds below: its sections start where that
build's do and the bytes at every pinned site are identical. A patch that leaves this code alone keeps
working, and one that moves or changes it is refused before anything is attached, so nothing in the game
is changed.

Every known build after the verified one carries a relocation: where each address of the verified build
lives in it. RelocateNativeBuild.py derives it read-only and accepts a code address only where the code
around it is identical apart from displacements; its report holds that build's sections and pins.

Regenerate the verified table with `python NativeCodeSignature.py <tlou-ii.exe>` after adding a site to the
native scripts, then rerun RelocateNativeBuild.py for every other build.
"""
from dataclasses import dataclass
from functools import cache
from pathlib import Path
import hashlib
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from InspectPeVa import PeError, read_layout, va_to_offset

VERIFIED_SHA256 = 'CAC7F729EAE6FAB743616B8216ACF20E9F05D8E51DA0E3B7A4892147569DD357'
UNVERIFIED_BUILD = 'Game code differs from the verified build'
# Every code site of the native modes is pinned with the 32 bytes the scripts fingerprint.
CODE_BYTES = 32
# Read-only skill observation: player handle, persistent quantities, upgrade export, game-info and difficulty.
SKILL_RVAS = (0xDA8DC0, 0xBC3470, 0xAB3CA0, 0xD2AD20, 0x1E41510, 0x126CFD0, 0x385CA0)
# Return addresses the recovery bridge compares callers against, pinned around the call they follow,
# and the profile stat service's vtable through slot 9, whose setter the bridge calls.
EXTRA_SITES = ((0xFD7909 - 8, 16), (0x1B802C3 - 8, 16), (0x1B8152F - 8, 16), (0x1B82059 - 8, 16),
               (0x1BE144E - 8, 16), (0x2AF57D8, 0x50))
# Every return address a native mode compares a caller with: the recovery bridge's, and one only the trigger
# experiments compare.
BRIDGE_RETURNS = (0xFD7909, 0x1B802C3, 0x1B8152F, 0x1B82059, 0x1BE144E)
RETURN_ADDRESSES = BRIDGE_RETURNS + (0x133EC36,)
# Data the product's native modes read, compare or hand to the game: the run and save managers, the task
# context, the broadcast target, the profile stat service and its vtable, and the results UI's manager, hash
# table and page types. None of it is pinned: it is game state, not code.
PRODUCT_DATA = (0x415AC90, 0x9334770, 0x9341660, 0x9342080, 0x9398730, 0x9398790, 0x4248B78, 0x2AF57D8,
                0x92D08D8, 0x37D6928, 0x37D69B8, 0x9493978, 0x9493BA8, 0x9497E88,
                0x411A770, 0x410EBD0, 0x4248B30)
# With the Rogue and mode flags the experiments read.
DATA_ADDRESSES = PRODUCT_DATA + (0x415AD30, 0x4161F58)
VTABLES = ((0x2AF57D8, 0x50),)


def code_addresses():
    """Every code address a native mode hooks, calls, or compares a return address with."""
    from NativeCheckpointTrigger import (CODE_RVAS, COLLECTION_RVAS, ENCOUNTER_RVAS, LIFECYCLE_RVAS, LOAD_RVAS,
                                         RECOVERY_RVAS, RESULTS_RVAS, RESUME_RVAS)
    from NativeEncounterRestart import CONTROL_RVAS
    from NativeResultsExit import CONTINUE_RVAS
    return frozenset(rva for group in (CODE_RVAS, LIFECYCLE_RVAS, RESUME_RVAS, LOAD_RVAS, COLLECTION_RVAS,
                                       RECOVERY_RVAS, RESULTS_RVAS, ENCOUNTER_RVAS, CONTROL_RVAS, CONTINUE_RVAS,
                                       RETURN_ADDRESSES, SKILL_RVAS) for rva in group)


@cache
def native_addresses():
    """Every address of the verified build the native scripts use."""
    return code_addresses() | frozenset(DATA_ADDRESSES)


@cache
def product_addresses():
    """The addresses Guardian's native modes use: recovery, the results continue, encounter control and the
    departure recorder. Every later build must have all of them; the rest serve experiments, which refuse any
    build but the verified one (NativeCheckpointProbe.require_verified_build)."""
    from NativeCheckpointTrigger import ENCOUNTER_RVAS, RECOVERY_RVAS, RESULTS_RVAS
    from NativeEncounterRestart import CONTROL_RVAS
    from NativeResultsExit import CONTINUE_RVAS
    return frozenset(rva for group in (RECOVERY_RVAS, RESULTS_RVAS, ENCOUNTER_RVAS, CONTROL_RVAS, CONTINUE_RVAS,
                                       BRIDGE_RETURNS, PRODUCT_DATA, SKILL_RVAS) for rva in group)


@dataclass(frozen=True)
class Build:
    """A build of the game the native scripts know."""
    name: str
    sections: dict
    pinned: dict
    # Where each address of the verified build lives in this build; None for the verified build itself.
    relocation: dict = None

    def mismatch(self, image):
        """None when the image is this build at every pinned site, otherwise where it differs."""
        try:
            preferred, headers, sections = read_layout(image)
            starts = {section.name: section.virtual_address for section in sections}
            if starts != self.sections:
                return 'section layout'
            for rva, expected in self.pinned.items():
                offset, _ = va_to_offset(preferred + rva, preferred, headers, sections)
                if image[offset:offset + len(expected) // 2].hex() != expected:
                    return f'code at {rva:#x}'
        except PeError as error:
            return f'unreadable executable: {error}'
        return None

    def has(self, rva):
        return rva in native_addresses() and (self.relocation is None or rva in self.relocation)

    def at(self, rva):
        """This build's RVA for an address of the verified build."""
        if not self.has(rva):
            raise ValueError(f'{rva:#x} is not a native address of the {self.name} build')
        return rva if self.relocation is None else self.relocation[rva]

# Generated from the verified build (see the module docstring).
SECTIONS = {'.text': 0x1000, '.rdata': 0x2a86000, '.data': 0x3539000, '.pdata': 0x9652000, '_RDATA': 0x9841000, '.rsrc': 0x9855000, '.reloc': 0x99c6000}
PINNED = {
    0x385ca0: '4883ec28803dd052cf03007410488d0ddce3ce034883c428e973785c0033d233',
    0xab3ca0: '4885d27422448b0933c04585c974184c8b5108448bc0496bc8384a391411740a',
    0xbc3470: '488d4110c3cccccccccccccccccccccc488d4110c3cccccccccccccccccccccc',
    0xd2ad20: '4056574883ec38488b05ea63b5024833c44889442428498bf8488bf24885d20f',
    0xd49530: '405356574881ec80000000488b05d67bb3024833c44889442470488bf1c5f9ef',
    0xd49630: '405356574881ec80000000488b05d67ab3024833c44889442470488bf1c5f9ef',
    0xd95530: '488bc448894808555356574154415541564157488d68a14881eca8000000c5f8',
    0xd966f0: '48895c2410574883ec30488bd9488d052401f60148890133d2e8c2f8d3ff33d2',
    0xda8dc0: '4883ec38488b054d83ad024833c44889442428e838b3480084c07427b90e0000',
    0xdacc20: '488bc453565741544155415641574881ecf0040000c5f82970b8c5f82978a8c5',
    0xdaf950: '48895c24205556574154415541564157488d6c24d94881ecb0000000488b05a5',
    0xdafea0: '48895c240848896c24104889742418574883ec40498bf1498bf8488bea488bd9',
    0xdc1780: '4053565741544155415641574881ec70020000c5f829b42460020000488b0575',
    0xfd7870: '40534881ecb0000000488b0598988a024833c448898424a00000004c8b055634',
    0xfd7901: '331803e8b7790600c5f9efc0c5fc1144',
    0xfdcd90: '4c8bdc49895b10574881ec80030000488b5908c683120e0b000133ff89791840',
    0xfddbf0: '48895c24185556574154415541564157488dac24c0f5ffff4881ec400b0000c5',
    0xfe7b10: '4883ec28e84716b5004c8bc833c04d85c97423448b05ee501503498b4940418b',
    0xfe8fc0: '48895c2410488974241848897c2420554154415541564157488d6c24c94881ec',
    0xfe96f0: '40534883ec20488bd948899150140b0044888149140b00e8f403b500488b15c5',
    0xff6920: '48896c2410488974241848897c242041564883ec60488b05dca788024833c448',
    0x103f2c0: '48895c24104889742418574883ec308bfa488bd980b9c8720000000f859a0200',
    0x1042680: '48895c2410488974241848897c24205541564157488d6c24a04881ec60010000',
    0x1042aa0: '40534883ec60488b056be683024833c448894424584533c0488d0db11c2f0833',
    0x1042b80: '40574883ec40488b058be583024833c448894424304533c0488d0dd11b2f0833',
    0x10434c0: '4883ec68488b054ddc83024833c448894424584533c0488d0d93122f0833d2e8',
    0x1046290: '48895c2410488974241848897c2420554154415541564157488dac2420feffff',
    0x1046f80: '4883ec284533c0488d0de2d72e0833d2e8cb7ab2004885c07413488b80200100',
    0x1046fc0: '4883ec284533c0488d0da2d72e0833d2e88b7ab2004885c07413488b80200100',
    0x1048520: '48895c2408574883ec20c681c872000000488bd9c78120050000020000004881',
    0x1048960: '488951504c894158c3ccccccccccccccc4c17c1000c5fc1181c80400008991e8',
    0x1049bf0: '48895c2410488974241848897c2420554154415541564157488d6c24904881ec',
    0x104c700: '33c0380508e610030f95c0488901488bc1c3cccccccccccccccccccccccccccc',
    0x105e930: '48895c2408574883ec20803d17361003000fb6da488bf97421488d0d40c30f03',
    0x105f830: '48897c24185541564157488d6c24b94881ecb0000000488b05cb1882024833c4',
    0x11f9300: '48895c240848896c24104889742418574883ec30418bd88bfa488bf1488d2d3d',
    0x1200ac0: '48895c241055565741544155415641574881ec90000000c5f829b42480000000',
    0x120af40: '40555356574154415541564157488d6c24e84881ec18010000488b05b8616702',
    0x120d090: '40535557415441574883ec408b9c24980000004d8be18bbc24900000004d8bf8',
    0x120dac0: '488b02488b400848051b0400004883e0f8c3cccccccccccccccccccccccccccc',
    0x1247360: '40534883ec20488bc2488bd94885d27423803a00741e48ba25232284e49cf2cb',
    0x124c1d0: '0fb60184c0741f49b8b3010000000100000fb6c0488d49014833d00fb601490f',
    0x126cfd0: '8bc1488d0d57bbfd02488b04c1c3cccc448bc9488d0d46bbfd024a8b04c94885',
    0x133d910: '488b81800000004885c0740e488b40304885c07405488b4008c333c0c3cccccc',
    0x133d950: '488b81800000004885c0740e488b40384885c07405488b4008c333c0c3cccccc',
    0x133ebb0: '40534883ec20488bd9e872d02200c5f057c9c5f82fc1770a807b09000f8c9e00',
    0x133ec5e: '806308df900fb64309c0e8052401884332904883c4205bc3cccccccccccccccc',
    0x1b37160: '4053574883ec78488b05aa9fd4014833c44889442468488bf9c5f9efc033c0c5',
    0x1b37310: '405356574881ec80000000488b05f69dd4014833c44889442470488bf9c5f9ef',
    0x1b39bf0: '4883ec28488b0dbdcdc9014c8bc233d2418bc0f7f14863c2488b1519cdc90148',
    0x1b6e000: '48895c2410564883ec30448b8160020000488bda33d2488bf14585c07457448b',
    0x1b6ea60: '48895c240848896c24104889742418574883ec20803df55c7c0700410fb6f00f',
    0x1b6fa70: '488bc44889581048896818488970205741544155415641574883ec70410fb6d9',
    0x1b7d080: '4883b9a805000000742133c04883c1588b1185d2740583fa077c1348ffc04881',
    0x1b7fc60: '40555356574154415541564157488d6c24d84881ec280100004963f9897c2434',
    0x1b80080: '40534881ec9000000033c0c7442438ffffffff83b9e005000014488bd9488944',
    0x1b802bb: 'c5f877e89df9ffff84c074074488bbc8',
    0x1b81527: 'c5f877e831e7ffff4881c498000000c3',
    0x1b82051: '8b4f08e827d82a0084c00f85e6000000',
    0x1b83f70: '48895c240848896c242056574154415641574883ec70488b058bd1cf014833c4',
    0x1b843a0: '48895c240848896c241056574154415641574881ec80010000488b0558cdcf01',
    0x1bdb630: '48895c24185556574883ec70488b05d55aca014833c44889442460410fb6d941',
    0x1bdeed0: '48895c24105556574154415541564157488dac2430ffffff4881ecd001000048',
    0x1bdf840: '4883ec38488b02488bd14889442448488d0d1a4f7507488b4424604889442428',
    0x1be1446: '0000ff9080000000488b742448488b6c',
    0x1c55a3c: '498b16488bc8488bf0e8a603000085c0780f4863c8488b4630488d14498b7cd0',
    0x1c55df0: '48895c2410488974241855574156488d6c24b94881ecb0000000448b91800200',
    0x1c57bb0: '48895c241048896c2418565741574883ec20448bfa410fb6e9498bd0498bd848',
    0x1e2f880: '4883ec3883790c02488bc2740732c04883c438c3448b414841b902000000488b',
    0x1e35d60: '33c04885c97425448b0d0adc6507488b5140458bc149c1e9064183e03fc422bb',
    0x1e39660: '33c04885c97425448b0d3aa56507488b5140458bc149c1e9064183e03fc422bb',
    0x1e39880: '4883ec28e8f75a0100488bc8e8cfc4ffff4885c0741ef680a108000001741748',
    0x1e3b920: '48895c242055565741544155415641574883ec60488b05dd57a4014833c44889',
    0x1e41510: '48895c2418565741564883ec20488bfa4c8bf1488bcfba02000000e8705941ff',
    0x1e4df10: '48895c24184889742420574881ec50040000488b05ef31a3014833c448898424',
    0x1e4f190: '4883ec28e877e74eff4885c07427448b05e38c6407488b4840418bd048c1ea06',
    0x1e4f380: '40534883ec20e805feffff488bd84885c07420488bc8e8d50000004885c07410',
    0x1e4f470: '4883ec28e8d7e44eff4885c07427448b05038a6407488b4840418bd048c1ea06',
    0x2af57d8: '70a9334001000000e0aa33400100000060ab33400100000080ab334001000000b07bc541010000005079c54101000000c066c54101000000b065c541010000005051c541010000004072c54101000000',
}

VERIFIED = Build('verified', SECTIONS, PINNED)
# Steam build 19297819, game version v1.6.10721 (June 2026). Relocated from the verified build by
# RelocateNativeBuild.py, with the changed functions scripts/native-builds.json records as reviewed.
V1_6_10721 = Build(
    'v1.6.10721',
    {'.text': 0x1000, '.rdata': 0x2a8c000, '.data': 0x3541000, '.pdata': 0x965a000, '_RDATA': 0x984a000, '.rsrc': 0x985e000, '.reloc': 0x99cf000},
    {
        # Read-only Ellie skill-state sites relocated from the verified build.
        0x388ae0: '4883ec28803dd0a4cf03007410488d0ddc35cf034883c428e9537a5c0033d233',
        0xab6cc0: '4885d27422448b0933c04585c974184c8b5108448bc0496bc8384a391411740a',
        0xbc64d0: '488d4110c3cccccccccccccccccccccc488d4110c3cccccccccccccccccccccc',
        0xd2de00: '4056574883ec38488b052ab4b5024833c44889442428498bf8488bf24885d20f',
        0xdabea0: '4883ec38488b058dd3ad024833c44889442428e808bc480084c07427b90e0000',
        0x1270a80: '8bc1488d0d2701fe02488b04c1c3cccc448bc9488d0d1601fe024a8b04c94885',
        0x1e47410: '48895c2418565741564883ec20488bfa4c8bf1488bcfba02000000e8203541ff',
        0xd4c610: '405356574881ec80000000488b0516ccb3024833c44889442470488bf1c5f9ef',
        0xd4c710: '405356574881ec80000000488b0516cbb3024833c44889442470488bf1c5f9ef',
        0xd98610: '488bc448894808555356574154415541564157488d68a14881eca8000000c5f8',
        0xd997d0: '48895c2410574883ec30488bd9488d05a431f60148890133d2e802f8d3ff33d2',
        0xdb2a30: '48895c24205556574154415541564157488d6c24d94881ecb0000000488b05e5',
        0xdb2f80: '48895c240848896c24104889742418574883ec40498bf1498bf8488bea488bd9',
        0xdc4860: '4053565741544155415641574881ec70020000c5f829b42460020000488b05b5',
        0xfdab10: '40534881ecb0000000488b0518e78a024833c448898424a00000004c8b05f681',
        0xfdaba1: '811803e8c77b0600c5f9efc0c5fc1144',
        0xfeadd0: '4883ec28e81734b5004c8bc833c04d85c97423448b056e9e1503498b4940418b',
        0xfeca10: '40534883ec20488bd948899150140b0044888149140b00e86421b500488b1525',
        0x1042770: '48895c24104889742418574883ec308bfa488bd980b9c8720000000f859a0200',
        0x1045b30: '48895c2410488974241848897c24205541564157488d6c24a04881ec60010000',
        0x1045f50: '40534883ec60488b05db3284024833c448894424584533c0488d0de1682f0833',
        0x1046030: '40574883ec40488b05fb3184024833c448894424304533c0488d0d01682f0833',
        0x1046970: '4883ec68488b05bd2884024833c448894424584533c0488d0dc35e2f0833d2e8',
        0x1049740: '48895c2410488974241848897c2420554154415541564157488dac2420feffff',
        0x104a430: '4883ec284533c0488d0d12242f0833d2e8cb96b2004885c07413488b80200100',
        0x104a470: '4883ec284533c0488d0dd2232f0833d2e88b96b2004885c07413488b80200100',
        0x104b9d0: '48895c2408574883ec20c681c872000000488bd9c78120050000020000004881',
        0x104be10: '488951504c894158c3ccccccccccccccc4c17c1000c5fc1181c80400008991e8',
        0x104d0a0: '48895c2410488974241848897c2420554154415541564157488d6c24904881ec',
        0x104fbb0: '33c03805983111030f95c0488901488bc1c3cccccccccccccccccccccccccccc',
        0x1061eb0: '48895c2408574883ec20803dd7801003000fb6da488bf97421488d0d000e1003',
        0x1062e10: '48897c24185541564157488d6c24b94881ecb0000000488b050b6482024833c4',
        0x11fca90: '48895c240848896c24104889742418574883ec30418bd88bfa488bf1488d2d8d',
        0x1204260: '48895c241055565741544155415641574881ec90000000c5f829b42480000000',
        0x120e7e0: '40555356574154415541564157488d6c24e84881ec18010000488b0538aa6702',
        0x1210960: '40535557415441574883ec408b9c24980000004d8be18bbc24900000004d8bf8',
        0x1211390: '488b02488b400848051b0400004883e0f8c3cccccccccccccccccccccccccccc',
        0x124acf0: '40534883ec20488bc2488bd94885d27423803a00741e48ba25232284e49cf2cb',
        0x124fc80: '0fb60184c0741f49b8b3010000000100000fb6c0488d49014833d00fb601490f',
        0x1340b10: '488b81800000004885c0740e488b40304885c07405488b4008c333c0c3cccccc',
        0x1340b50: '488b81800000004885c0740e488b40384885c07405488b4008c333c0c3cccccc',
        0x1341db0: '40534883ec20488bd9e8d2d62200c5f057c9c5f82fc1770a807b09000f8c9e00',
        0x1341e5e: '806308df900fb64309c0e8052401884332904883c4205bc3cccccccccccccccc',
        0x1b3c1f0: '4053574883ec78488b053ad0d4014833c44889442468488bf9c5f9efc033c0c5',
        0x1b3c3a0: '405356574881ec80000000488b0586ced4014833c44889442470488bf9c5f9ef',
        0x1b3ec80: '4883ec28488b0d9dfdc9014c8bc233d2418bc0f7f14863c2488b15f9fcc90148',
        0x1b730b0: '48895c2410564883ec30448b8160020000488bda33d2488bf14585c07457448b',
        0x1b73b10: '48895c240848896c24104889742418574883ec20803d258d7c0700410fb6f00f',
        0x1b83e00: '4883b9a805000000742133c04883c1588b1185d2740583fa077c1348ffc04881',
        0x1b86a00: '40555356574154415541564157488d6c24d84881ec280100004963f9897c2434',
        0x1b86e20: '40534881ec9000000033c0c7442438ffffffff83b9e005000014488bd9488944',
        0x1b8705b: 'c5f877e89df9ffff84c074074488bbc8',
        0x1b882c7: 'c5f877e831e7ffff4881c498000000c3',
        0x1b88e18: '8b4f08e860c92a0084c00f85e6000000',
        0x1b8ada0: '48895c240848896c242056574154415641574883ec70488b057be4cf014833c4',
        0x1b8b1d0: '48895c240848896c241056574154415641574881ec80010000488b0548e0cf01',
        0x1be08c0: '48895c24185556574883ec70488b056589ca014833c44889442460410fb6d941',
        0x1be4950: '48895c24105556574154415541564157488dac2420ffffff4881ece001000048',
        0x1be5490: '4883ec38488b02488bd14889442448488d0daa737507488b4424604889442428',
        0x1be72d6: '0000ff9080000000488b742448488b6c',
        0x1c5b87c: '498b16488bc8488bf0e8a603000085c0780f4863c8488b4630488d14498b7cd0',
        0x1c5bc30: '48895c2410488974241855574156488d6c24b94881ecb0000000448b91800200',
        0x1c5d9b0: '48895c241048896c2418565741574883ec20448bfa410fb6e9498bd0498bd848',
        0x1e35780: '4883ec3883790c02488bc2740732c04883c438c3448b414841b902000000488b',
        0x1e3bc60: '33c04885c97425448b0dcafe6507488b5140458bc149c1e9064183e03fc422bb',
        0x1e3f560: '33c04885c97425448b0dfac76507488b5140458bc149c1e9064183e03fc422bb',
        0x1e3f780: '4883ec28e8474f0100488bc8e8cfc4ffff4885c0741ef680a108000001741748',
        0x1e41820: '48895c242055565741544155415641574883ec60488b05fd79a4014833c44889',
        0x1e53260: '48895c24184889742420574881ec50040000488b05bf5fa3014833c448898424',
        0x1e544e0: '4883ec28e827c64eff4885c07427448b05d3ba6407488b4840418bd048c1ea06',
        0x1e546d0: '40534883ec20e805feffff488bd84885c07420488bc8e8d50000004885c07410',
        0x1e547c0: '4883ec28e887c34eff4885c07427448b05f3b76407488b4840418bd048c1ea06',
        0x2afb818: 'e0d533400100000050d7334001000000d0d7334001000000f0d7334001000000b0d9c5410100000050d7c5410100000000c5c54101000000f0c3c5410100000090afc5410100000080d0c54101000000',
    },
    {
        0xd49530: 0xd4c610,
        0xd49630: 0xd4c710,
        0xd95530: 0xd98610,
        0xd966f0: 0xd997d0,
        0xdaf950: 0xdb2a30,
        0xdafea0: 0xdb2f80,
        0xdc1780: 0xdc4860,
        0xfd7870: 0xfdab10,
        0xfd7909: 0xfdaba9,
        0xfe7b10: 0xfeadd0,
        0xfe96f0: 0xfeca10,
        0x103f2c0: 0x1042770,
        0x1042680: 0x1045b30,
        0x1042aa0: 0x1045f50,
        0x1042b80: 0x1046030,
        0x10434c0: 0x1046970,
        0x1046290: 0x1049740,
        0x1046f80: 0x104a430,
        0x1046fc0: 0x104a470,
        0x1048520: 0x104b9d0,
        0x1048960: 0x104be10,
        0x1049bf0: 0x104d0a0,
        0x104c700: 0x104fbb0,
        0x105e930: 0x1061eb0,
        0x105f830: 0x1062e10,
        0x11f9300: 0x11fca90,
        0x1200ac0: 0x1204260,
        0x120af40: 0x120e7e0,
        0x120d090: 0x1210960,
        0x120dac0: 0x1211390,
        0x1247360: 0x124acf0,
        0x124c1d0: 0x124fc80,
        0x133d910: 0x1340b10,
        0x133d950: 0x1340b50,
        0x133ebb0: 0x1341db0,
        0x133ec36: 0x1341e36,
        0x133ec5e: 0x1341e5e,
        0x1b37160: 0x1b3c1f0,
        0x1b37310: 0x1b3c3a0,
        0x1b39bf0: 0x1b3ec80,
        0x1b6e000: 0x1b730b0,
        0x1b6ea60: 0x1b73b10,
        0x1b7d080: 0x1b83e00,
        0x1b7fc60: 0x1b86a00,
        0x1b80080: 0x1b86e20,
        0x1b802c3: 0x1b87063,
        0x1b8152f: 0x1b882cf,
        0x1b82059: 0x1b88e20,
        0x1b83f70: 0x1b8ada0,
        0x1b843a0: 0x1b8b1d0,
        0x1bdb630: 0x1be08c0,
        0x1bdeed0: 0x1be4950,
        0x1bdf840: 0x1be5490,
        0x1be144e: 0x1be72de,
        0x1c55a3c: 0x1c5b87c,
        0x1c55df0: 0x1c5bc30,
        0x1c57bb0: 0x1c5d9b0,
        0x1e2f880: 0x1e35780,
        0x1e35d60: 0x1e3bc60,
        0x1e39660: 0x1e3f560,
        0x1e39880: 0x1e3f780,
        0x1e3b920: 0x1e41820,
        0x1e4df10: 0x1e53260,
        0x1e4f190: 0x1e544e0,
        0x1e4f380: 0x1e546d0,
        0x1e4f470: 0x1e547c0,
        0x2af57d8: 0x2afb818,
        0x385ca0: 0x388ae0,
        0xab3ca0: 0xab6cc0,
        0xbc3470: 0xbc64d0,
        0xd2ad20: 0xd2de00,
        0xda8dc0: 0xdabea0,
        0x126cfd0: 0x1270a80,
        0x1e41510: 0x1e47410,
        0x410ebd0: 0x4116c10,
        0x411a770: 0x41227b0,
        0x37d6928: 0x37de998,
        0x37d69b8: 0x37dea28,
        0x415ac90: 0x4162cd0,
        0x415ad30: 0x4162d70,
        0x4161f58: 0x4169f98,
        0x4248b78: 0x4250bf8,
        0x4248b30: 0x4250bb0,
        0x92d08d8: 0x92d89b8,
        0x9334770: 0x933c850,
        0x9341660: 0x934bfa0,
        0x9342080: 0x934c9c0,
        0x9398730: 0x93a0910,
        0x9398790: 0x93a0970,
        0x9493978: 0x949bb38,
        0x9493ba8: 0x949bd68,
        0x9497e88: 0x949ffc8,
    })
BUILDS = (VERIFIED, V1_6_10721)


def match_build(image):
    """(build, None) for the known build the image matches at every pinned site, else (None, where it differs
    from the known build whose sections it shares, or 'section layout')."""
    reasons = []
    for build in BUILDS:
        reason = build.mismatch(image)
        if reason is None:
            return build, None
        reasons.append(reason)
    return None, next((reason for reason in reasons if reason != 'section layout'), 'section layout')


def signature_mismatch(image):
    """None when the image is a known build at every pinned site, otherwise where it differs."""
    return match_build(image)[1]


# The build of the game this process attached to, set once NativeCheckpointProbe.verify_build accepted it.
# Each native script serves one game process, and no address is available before its build is verified.
_active = None


def activate(build):
    global _active
    _active = build


def active():
    if _active is None:
        raise ValueError("The game's build has not been verified")
    return _active


def at(rva):
    """The attached build's RVA for an address of the verified build."""
    return active().at(rva)


def addresses():
    """Every native address the attached build has, for the Frida scripts: verified-build hex -> RVA."""
    build = active()
    return {hex(rva): build.at(rva) for rva in sorted(native_addresses()) if build.has(rva)}


def main(argv):
    from NativeCheckpointTrigger import (CODE_RVAS, COLLECTION_RVAS, ENCOUNTER_RVAS, LIFECYCLE_RVAS, LOAD_RVAS,
                                         RECOVERY_RVAS, RESULTS_RVAS, RESUME_RVAS)
    image = Path(argv[1]).read_bytes()
    if hashlib.sha256(image).hexdigest().upper() != VERIFIED_SHA256:
        raise SystemExit('Generate the table from the verified build only')
    preferred, headers, sections = read_layout(image)
    sites = {rva: CODE_BYTES for group in (CODE_RVAS, LIFECYCLE_RVAS, RESUME_RVAS, LOAD_RVAS, COLLECTION_RVAS,
                                           RECOVERY_RVAS, RESULTS_RVAS, ENCOUNTER_RVAS, SKILL_RVAS) for rva in group}
    sites.update(EXTRA_SITES)
    print('SECTIONS = {' + ', '.join(f'{section.name!r}: {section.virtual_address:#x}' for section in sections) + '}')
    print('PINNED = {')
    for rva, size in sorted(sites.items()):
        offset, _ = va_to_offset(preferred + rva, preferred, headers, sections)
        print(f"    {rva:#x}: '{image[offset:offset + size].hex()}',")
    print('}')


if __name__ == '__main__':
    main(sys.argv)
