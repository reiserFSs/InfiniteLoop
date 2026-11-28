-- AscNet Menu command spec (JSON inside a Lua long string): the one source for the menu's pages, the chat lines it sends and its
-- text in en / zh-TW / ko / ja / zh-CN. The server test --gm-panel-spec-only parses this JSON and checks it against the
-- server's Command attributes (AscNet.GameServer/Commands): rename an argument, command or sub-action there and the test fails.
-- groups[].commands[]: cmd = chat command name; actions[]: id, line = chat tokens ({field} placeholders; empty optional fields
-- are dropped), fields[]: id, arg = server property name, kind = number|text|choice, regex = that property's server pattern
-- (verbatim), choices / allow (extra accepted words for number), optional, multiword, sample (a value the server accepts),
-- label = text key, hint = optional text key shown as the input's placeholder; confirm = text key of the warning shown before a destructive action runs.
-- lookup = id search kind (XAscNetMenuLookup: the client's own localized names; the field still holds the plain id), lookupBy = id of a
-- sibling choice field whose value is the lookup kind (unlock).
-- A War Zone `type` choice lists exactly the encounter types the server has enemy data for (the test compares them with the server's).
-- Machine-translated (not native-reviewed) texts: u.runconfirm, f.*.hint, the shortened labels of the second design pass and everything for /character set|max, /equip enhance, /cub and /unlock.
-- Text keys: g.<group id>, c.<cmd>, c.<cmd>.help, a.<cmd>.<action id>, f.* fields, w.* warnings, u.* panel strings.
return require("XCommon/Json").decode([==[
{
 "groups": [
  {
   "id": "items",
   "commands": [
    {
     "cmd": "item",
     "actions": [
      {
       "id": "add",
       "line": [
        "add",
        "{target}",
        "{amount}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.itemId",
         "lookup": "item",
         "sample": "1",
         "regex": "^[0-9]+$|^all$"
        },
        {
         "id": "amount",
         "arg": "Amount",
         "kind": "number",
         "label": "f.amount",
         "hint": "f.amount.hint",
         "sample": "100",
         "regex": "^[0-9]+$|^max$",
         "allow": [
          "max"
         ]
        }
       ]
      },
      {
       "id": "addall",
       "line": [
        "add",
        "all",
        "{amount}"
       ],
       "fields": [
        {
         "id": "amount",
         "arg": "Amount",
         "kind": "number",
         "label": "f.amount",
         "hint": "f.amount.hint",
         "sample": "100",
         "regex": "^[0-9]+$|^max$",
         "allow": [
          "max"
         ]
        }
       ]
      },
      {
       "id": "clear",
       "line": [
        "clear",
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.itemId",
         "lookup": "item",
         "sample": "1",
         "regex": "^[0-9]+$|^all$"
        }
       ],
       "confirm": "w.items"
      },
      {
       "id": "clearall",
       "line": [
        "clear",
        "all"
       ],
       "confirm": "w.items"
      },
      {
       "id": "reset",
       "line": [
        "reset",
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.itemId",
         "lookup": "item",
         "sample": "1",
         "regex": "^[0-9]+$|^all$"
        }
       ],
       "confirm": "w.itemreset"
      },
      {
       "id": "resetall",
       "line": [
        "reset",
        "all"
       ],
       "confirm": "w.itemreset"
      }
     ]
    },
    {
     "cmd": "bc",
     "actions": [
      {
       "id": "grant",
       "line": [
        "{amount}"
       ],
       "fields": [
        {
         "id": "amount",
         "arg": "Amount",
         "kind": "number",
         "label": "f.amount",
         "hint": "f.amount.hint",
         "sample": "100",
         "regex": "^[1-9][0-9]*$|^max$",
         "allow": [
          "max"
         ],
         "optional": true
        }
       ]
      }
     ]
    }
   ]
  },
  {
   "id": "chars",
   "commands": [
    {
     "cmd": "character",
     "actions": [
      {
       "id": "add",
       "line": [
        "add",
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "character",
         "sample": "1021001",
         "regex": "^[0-9]+$|^all$"
        }
       ]
      },
      {
       "id": "addall",
       "line": [
        "add",
        "all"
       ]
      },
      {
       "id": "max",
       "line": [
        "max",
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "owned-character",
         "hint": "f.characterId.hint",
         "sample": "1021001",
         "regex": "^[0-9]+$|^all$",
         "allow": [
          "all"
         ]
        }
       ],
       "confirm": "w.charmax"
      },
      {
       "id": "maxgear",
       "line": [
        "max",
        "{target}",
        "gear"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "owned-character",
         "hint": "f.characterId.hint",
         "sample": "1021001",
         "regex": "^[0-9]+$|^all$",
         "allow": [
          "all"
         ]
        }
       ],
       "confirm": "w.charmax"
      },
      {
       "id": "set",
       "line": [
        "set",
        "{target}",
        "{stat}",
        "{value}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "owned-character",
         "hint": "f.characterId.hint",
         "sample": "1021001",
         "regex": "^[0-9]+$|^all$"
        },
        {
         "id": "stat",
         "arg": "Field",
         "kind": "choice",
         "label": "f.stat",
         "sample": "level",
         "regex": "^(gear|level|grade|quality|star|awaken|trust|skill)$",
         "choices": [
          "level",
          "grade",
          "quality",
          "star",
          "awaken",
          "trust"
         ]
        },
        {
         "id": "value",
         "arg": "First",
         "kind": "number",
         "label": "f.value",
         "sample": "80",
         "regex": "^[0-9]+$"
        }
       ]
      },
      {
       "id": "setskill",
       "line": [
        "set",
        "{target}",
        "skill",
        "{skill}",
        "{level}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "owned-character",
         "hint": "f.characterId.hint",
         "sample": "1021001",
         "regex": "^[0-9]+$|^all$"
        },
        {
         "id": "skill",
         "arg": "First",
         "kind": "number",
         "label": "f.skillId",
         "sample": "1",
         "regex": "^[0-9]+$"
        },
        {
         "id": "level",
         "arg": "Second",
         "kind": "number",
         "label": "f.level",
         "sample": "10",
         "regex": "^[0-9]+$"
        }
       ]
      }
     ]
    },
    {
     "cmd": "coating",
     "actions": [
      {
       "id": "unlock",
       "line": [
        "unlock",
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "character",
         "sample": "1021001",
         "regex": "^[0-9]+$|^all$"
        }
       ]
      },
      {
       "id": "unlockall",
       "line": [
        "unlock",
        "all"
       ]
      }
     ]
    },
    {
     "cmd": "equip",
     "actions": [
      {
       "id": "add",
       "line": [
        "add",
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.equipId",
         "lookup": "equip",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        }
       ]
      },
      {
       "id": "addall",
       "line": [
        "add",
        "all"
       ]
      },
      {
       "id": "prune",
       "line": [
        "prune"
       ],
       "confirm": "w.equip"
      },
      {
       "id": "sync",
       "line": [
        "sync"
       ]
      },
      {
       "id": "list",
       "line": [
        "list",
        "{filter}"
       ],
       "fields": [
        {
         "id": "filter",
         "arg": "Target",
         "kind": "choice",
         "label": "f.show",
         "sample": "weapons",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$",
         "choices": [
          "weapons",
          "memories",
          "all"
         ],
         "optional": true
        }
       ]
      },
      {
       "id": "inspect",
       "line": [
        "inspect",
        "{record}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.recordId",
         "lookup": "owned-equip",
         "hint": "f.recordId.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        }
       ]
      },
      {
       "id": "level",
       "line": [
        "level",
        "{target}",
        "{level}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "text",
         "label": "f.equipTarget",
         "lookup": "owned-equip",
         "hint": "f.equipTarget.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        },
        {
         "id": "level",
         "arg": "Value",
         "kind": "number",
         "label": "f.levelMax",
         "hint": "f.levelMax.hint",
         "sample": "80",
         "regex": "^[0-9]+$|^max$|^suit$",
         "allow": [
          "max"
         ]
        }
       ]
      },
      {
       "id": "breakthrough",
       "line": [
        "breakthrough",
        "{target}",
        "{step}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "text",
         "label": "f.equipTarget",
         "lookup": "owned-equip",
         "hint": "f.equipTarget.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        },
        {
         "id": "step",
         "arg": "Value",
         "kind": "number",
         "label": "f.step",
         "hint": "f.levelMax.hint",
         "sample": "5",
         "regex": "^[0-9]+$|^max$|^suit$",
         "allow": [
          "max"
         ]
        }
       ]
      },
      {
       "id": "resclear",
       "line": [
        "resonance",
        "{record}",
        "{slot}",
        "clear"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.recordId",
         "lookup": "owned-equip",
         "hint": "f.recordId.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        },
        {
         "id": "slot",
         "arg": "Value",
         "kind": "number",
         "label": "f.slot",
         "sample": "1",
         "regex": "^[0-9]+$|^max$|^suit$"
        }
       ]
      },
      {
       "id": "resattrib",
       "line": [
        "resonance",
        "{record}",
        "{slot}",
        "attrib",
        "{pool}",
        "{character}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.recordId",
         "lookup": "owned-equip",
         "hint": "f.recordId.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        },
        {
         "id": "slot",
         "arg": "Value",
         "kind": "number",
         "label": "f.slot",
         "sample": "1",
         "regex": "^[0-9]+$|^max$|^suit$"
        },
        {
         "id": "pool",
         "arg": "ResonanceId",
         "kind": "number",
         "label": "f.poolId",
         "sample": "1",
         "regex": "^[0-9]+$"
        },
        {
         "id": "character",
         "arg": "BoundCharacter",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "owned-character",
         "hint": "f.boundChar.hint",
         "sample": "1021001",
         "regex": "^[0-9]+$",
         "optional": true
        }
       ]
      },
      {
       "id": "resskill",
       "line": [
        "resonance",
        "{record}",
        "{slot}",
        "skill",
        "{skill}",
        "{character}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.recordId",
         "lookup": "owned-equip",
         "hint": "f.recordId.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        },
        {
         "id": "slot",
         "arg": "Value",
         "kind": "number",
         "label": "f.slot",
         "sample": "1",
         "regex": "^[0-9]+$|^max$|^suit$"
        },
        {
         "id": "skill",
         "arg": "ResonanceId",
         "kind": "number",
         "label": "f.skillId",
         "sample": "1",
         "regex": "^[0-9]+$"
        },
        {
         "id": "character",
         "arg": "BoundCharacter",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "owned-character",
         "hint": "f.boundChar.hint",
         "sample": "1021001",
         "regex": "^[0-9]+$",
         "optional": true
        }
       ]
      },
      {
       "id": "resweapon",
       "line": [
        "resonance",
        "{record}",
        "{slot}",
        "weaponskill",
        "{skill}",
        "{character}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.recordId",
         "lookup": "owned-equip",
         "hint": "f.recordId.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        },
        {
         "id": "slot",
         "arg": "Value",
         "kind": "number",
         "label": "f.slot",
         "sample": "1",
         "regex": "^[0-9]+$|^max$|^suit$"
        },
        {
         "id": "skill",
         "arg": "ResonanceId",
         "kind": "number",
         "label": "f.skillId",
         "sample": "1",
         "regex": "^[0-9]+$"
        },
        {
         "id": "character",
         "arg": "BoundCharacter",
         "kind": "number",
         "label": "f.characterId",
         "lookup": "owned-character",
         "hint": "f.boundChar.hint",
         "sample": "1021001",
         "regex": "^[0-9]+$",
         "optional": true
        }
       ]
      },
      {
       "id": "overrun",
       "line": [
        "overrun",
        "{record}",
        "{level}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.recordId",
         "lookup": "owned-equip",
         "hint": "f.recordId.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        },
        {
         "id": "level",
         "arg": "Value",
         "kind": "number",
         "label": "f.levelMax",
         "hint": "f.levelMax.hint",
         "sample": "5",
         "regex": "^[0-9]+$|^max$|^suit$",
         "allow": [
          "max"
         ]
        }
       ]
      },
      {
       "id": "overrunsuit",
       "line": [
        "overrun",
        "{record}",
        "suit",
        "{suit}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.recordId",
         "lookup": "owned-equip",
         "hint": "f.recordId.hint",
         "sample": "1",
         "regex": "^[0-9]+$|^all$|^c[0-9]+$|^weapons$|^memories$"
        },
        {
         "id": "suit",
         "arg": "Option",
         "kind": "number",
         "label": "f.suitId",
         "lookup": "equip-suit",
         "sample": "1",
         "regex": "^[0-9]+$|^clear$|^attrib$|^skill$|^weaponskill$"
        }
       ]
      }
     ]
    },
    {
     "cmd": "cub",
     "actions": [
      {
       "id": "list",
       "line": [
        "list",
        "{what}"
       ],
       "fields": [
        {
         "id": "what",
         "arg": "Target",
         "kind": "choice",
         "label": "f.cubList",
         "sample": "templates",
         "regex": "^([0-9]+|all|templates)$",
         "choices": [
          "templates"
         ],
         "optional": true
        }
       ]
      },
      {
       "id": "add",
       "line": [
        "add",
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.cubTemplate",
         "lookup": "cub",
         "hint": "f.cubTemplate.hint",
         "sample": "1",
         "regex": "^([0-9]+|all|templates)$",
         "allow": [
          "all"
         ]
        }
       ]
      },
      {
       "id": "level",
       "line": [
        "level",
        "{record}",
        "{level}",
        "{overclock}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.cubRecord",
         "lookup": "owned-cub",
         "hint": "f.cubRecord.hint",
         "sample": "1",
         "regex": "^([0-9]+|all|templates)$"
        },
        {
         "id": "level",
         "arg": "First",
         "kind": "number",
         "label": "f.level",
         "sample": "80",
         "regex": "^[0-9]+$"
        },
        {
         "id": "overclock",
         "arg": "Second",
         "kind": "number",
         "label": "f.overclock",
         "sample": "1",
         "regex": "^[0-9]+$",
         "optional": true
        }
       ]
      },
      {
       "id": "quality",
       "line": [
        "quality",
        "{record}",
        "{quality}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.cubRecord",
         "lookup": "owned-cub",
         "hint": "f.cubRecord.hint",
         "sample": "1",
         "regex": "^([0-9]+|all|templates)$"
        },
        {
         "id": "quality",
         "arg": "First",
         "kind": "number",
         "label": "f.quality",
         "sample": "5",
         "regex": "^[0-9]+$"
        }
       ]
      },
      {
       "id": "skill",
       "line": [
        "skill",
        "{record}",
        "{skill}",
        "{level}"
       ],
       "fields": [
        {
         "id": "record",
         "arg": "Target",
         "kind": "number",
         "label": "f.cubRecord",
         "lookup": "owned-cub",
         "hint": "f.cubRecord.hint",
         "sample": "1",
         "regex": "^([0-9]+|all|templates)$"
        },
        {
         "id": "skill",
         "arg": "First",
         "kind": "number",
         "label": "f.skillId",
         "sample": "1",
         "regex": "^[0-9]+$"
        },
        {
         "id": "level",
         "arg": "Second",
         "kind": "number",
         "label": "f.level",
         "sample": "5",
         "regex": "^[0-9]+$"
        }
       ]
      },
      {
       "id": "max",
       "line": [
        "max",
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "Target",
         "kind": "number",
         "label": "f.cubRecord",
         "lookup": "owned-cub",
         "hint": "f.cubRecord.hint",
         "sample": "1",
         "regex": "^([0-9]+|all|templates)$",
         "allow": [
          "all"
         ]
        }
       ],
       "confirm": "w.cubmax"
      }
     ]
    }
   ]
  },
  {
   "id": "progress",
   "commands": [
    {
     "cmd": "level",
     "actions": [
      {
       "id": "set",
       "line": [
        "{level}"
       ],
       "fields": [
        {
         "id": "level",
         "arg": "Level",
         "kind": "number",
         "label": "f.level",
         "sample": "60",
         "regex": "^[0-9]+$|^max$"
        }
       ],
       "confirm": "w.level"
      },
      {
       "id": "max",
       "line": [
        "max"
       ]
      }
     ]
    },
    {
     "cmd": "stage",
     "actions": [
      {
       "id": "clear",
       "line": [
        "{target}"
       ],
       "fields": [
        {
         "id": "target",
         "arg": "TargetStage",
         "kind": "number",
         "label": "f.stageId",
         "lookup": "stage",
         "sample": "10010101",
         "regex": "^[0-9]+$|^all$"
        }
       ]
      },
      {
       "id": "clearall",
       "line": [
        "all"
       ],
       "confirm": "w.stage"
      }
     ]
    },
    {
     "cmd": "scene",
     "actions": [
      {
       "id": "unlockall",
       "line": [
        "unlock",
        "all"
       ]
      }
     ]
    }
   ]
  },
  {
   "id": "modes",
   "commands": [
    {
     "cmd": "warzone",
     "actions": [
      {
       "id": "tiers",
       "line": [
        "tiers"
       ]
      },
      {
       "id": "tier",
       "line": [
        "tier",
        "{tier}"
       ],
       "fields": [
        {
         "id": "tier",
         "arg": "Value",
         "kind": "text",
         "label": "f.tier",
         "hint": "f.tier.hint",
         "sample": "elite",
         "regex": "^[A-Za-z0-9_\\-]+%?$"
        }
       ]
      },
      {
       "id": "zones",
       "line": [
        "zones",
        "{filter}",
        "{type}"
       ],
       "fields": [
        {
         "id": "filter",
         "arg": "Value",
         "kind": "text",
         "label": "f.filter",
         "hint": "f.filter.hint",
         "sample": "ice",
         "regex": "^[A-Za-z0-9_\\-]+%?$",
         "optional": true
        },
        {
         "id": "type",
         "arg": "Type",
         "kind": "choice",
         "label": "f.type",
         "sample": "duo",
         "regex": "^[A-Za-z0-9_\\-]+$",
         "choices": [
          "duo"
         ],
         "optional": true
        }
       ]
      },
      {
       "id": "set",
       "line": [
        "set",
        "{zone}",
        "{type}"
       ],
       "fields": [
        {
         "id": "zone",
         "arg": "Value",
         "kind": "text",
         "label": "f.zone",
         "hint": "f.zone.hint",
         "sample": "ice",
         "regex": "^[A-Za-z0-9_\\-]+%?$",
         "optional": true
        },
        {
         "id": "type",
         "arg": "Type",
         "kind": "choice",
         "label": "f.type",
         "sample": "duo",
         "regex": "^[A-Za-z0-9_\\-]+$",
         "choices": [
          "duo"
         ],
         "optional": true
        }
       ]
      },
      {
       "id": "zone",
       "line": [
        "zone",
        "{area}"
       ],
       "fields": [
        {
         "id": "area",
         "arg": "Value",
         "kind": "number",
         "label": "f.areaId",
         "sample": "1",
         "regex": "^[A-Za-z0-9_\\-]+%?$"
        }
       ]
      },
      {
       "id": "zoneauto",
       "line": [
        "zone",
        "auto"
       ]
      },
      {
       "id": "fluctuation",
       "line": [
        "fluctuation",
        "{level}"
       ],
       "fields": [
        {
         "id": "level",
         "arg": "Value",
         "kind": "number",
         "label": "f.fluctuation",
         "sample": "3",
         "regex": "^[A-Za-z0-9_\\-]+%?$"
        }
       ]
      },
      {
       "id": "dmg",
       "line": [
        "dmg",
        "{percent}",
        "{type}"
       ],
       "fields": [
        {
         "id": "percent",
         "arg": "Value",
         "kind": "number",
         "label": "f.percent",
         "sample": "120",
         "regex": "^[A-Za-z0-9_\\-]+%?$"
        },
        {
         "id": "type",
         "arg": "Type",
         "kind": "choice",
         "label": "f.type",
         "sample": "duo",
         "regex": "^[A-Za-z0-9_\\-]+$",
         "choices": [
          "duo"
         ],
         "optional": true
        }
       ],
       "confirm": "w.dmg"
      },
      {
       "id": "dmgreset",
       "line": [
        "dmg",
        "reset",
        "{type}"
       ],
       "fields": [
        {
         "id": "type",
         "arg": "Type",
         "kind": "choice",
         "label": "f.type",
         "sample": "duo",
         "regex": "^[A-Za-z0-9_\\-]+$",
         "choices": [
          "duo"
         ],
         "optional": true
        }
       ],
       "confirm": "w.dmg"
      },
      {
       "id": "reset",
       "line": [
        "reset"
       ],
       "confirm": "w.reset"
      }
     ]
    },
    {
     "cmd": "ppc",
     "actions": [
      {
       "id": "tiers",
       "line": [
        "tiers"
       ]
      },
      {
       "id": "tier",
       "line": [
        "tier",
        "{levelType}"
       ],
       "fields": [
        {
         "id": "levelType",
         "arg": "Value",
         "kind": "number",
         "label": "f.levelType",
         "sample": "1",
         "regex": "^(reset|[0-9]+|status|bosses|boss|weaknesses|weakness|zones|zone)$"
        }
       ]
      },
      {
       "id": "reset",
       "line": [
        "reset"
       ],
       "confirm": "w.reset"
      },
      {
       "id": "codexreset",
       "line": [
        "codex",
        "reset"
       ],
       "confirm": "w.reset"
      },
      {
       "id": "attempts",
       "line": [
        "attempts",
        "{count}"
       ],
       "fields": [
        {
         "id": "count",
         "arg": "Value",
         "kind": "number",
         "label": "f.count",
         "sample": "3",
         "regex": "^(reset|[0-9]+|status|bosses|boss|weaknesses|weakness|zones|zone)$"
        }
       ]
      },
      {
       "id": "status",
       "line": [
        "intensive",
        "status"
       ]
      },
      {
       "id": "bosses",
       "line": [
        "intensive",
        "bosses"
       ]
      },
      {
       "id": "boss",
       "line": [
        "intensive",
        "boss",
        "{boss}"
       ],
       "fields": [
        {
         "id": "boss",
         "arg": "ExtraValue",
         "kind": "text",
         "label": "f.boss",
         "sample": "1",
         "regex": "^\\S+$",
         "multiword": true
        }
       ]
      },
      {
       "id": "weaknesses",
       "line": [
        "intensive",
        "weaknesses"
       ]
      },
      {
       "id": "weakness",
       "line": [
        "intensive",
        "weakness",
        "{group}"
       ],
       "fields": [
        {
         "id": "group",
         "arg": "ExtraValue",
         "kind": "number",
         "label": "f.group",
         "sample": "1",
         "regex": "^\\S+$"
        }
       ]
      },
      {
       "id": "intensivereset",
       "line": [
        "intensive",
        "reset"
       ],
       "confirm": "w.reset"
      }
     ]
    }
   ]
  },
  {
   "id": "cosmetics",
   "commands": [
    {
     "cmd": "unlock",
     "actions": [
      {
       "id": "unlock",
       "line": [
        "{kind}",
        "{id}"
       ],
       "fields": [
        {
         "id": "kind",
         "arg": "Kind",
         "kind": "choice",
         "label": "f.kind",
         "sample": "medal",
         "regex": "^(medal|nameplate|emoji|title|chatboard|portrait|furniture)$",
         "choices": [
          "medal",
          "nameplate",
          "emoji",
          "title",
          "chatboard",
          "portrait"
         ]
        },
        {
         "id": "id",
         "arg": "Target",
         "kind": "number",
         "label": "f.unlockId",
         "lookupBy": "kind",
         "hint": "f.unlockId.hint",
         "sample": "1",
         "regex": "^([0-9]+|all)$",
         "allow": [
          "all"
         ]
        }
       ]
      },
      {
       "id": "furniture",
       "line": [
        "furniture",
        "{id}",
        "{count}"
       ],
       "fields": [
        {
         "id": "id",
         "arg": "Target",
         "kind": "number",
         "label": "f.furnitureId",
         "lookup": "furniture",
         "sample": "1",
         "regex": "^([0-9]+|all)$"
        },
        {
         "id": "count",
         "arg": "Count",
         "kind": "number",
         "label": "f.furnCount",
         "hint": "f.furnCount.hint",
         "sample": "1",
         "regex": "^[0-9]+$",
         "optional": true
        }
       ]
      }
     ]
    }
   ]
  },
  {
   "id": "tools",
   "commands": [
    {
     "cmd": "save",
     "actions": [
      {
       "id": "save",
       "line": []
      }
     ]
    },
    {
     "cmd": "help",
     "actions": [
      {
       "id": "help",
       "line": [
        "{command}"
       ],
       "fields": [
        {
         "id": "command",
         "arg": "CommandName",
         "kind": "text",
         "label": "f.command",
         "sample": "item",
         "regex": "^[^\\s]+$",
         "optional": true
        }
       ]
      }
     ]
    }
   ]
  }
 ],
 "text": {
  "g.items": {"en": "Items & currency", "zh-TW": "道具與貨幣", "ko": "아이템 및 재화", "ja": "アイテム・通貨", "zh-CN": "道具与货币"},
  "g.chars": {"en": "Characters & equipment", "zh-TW": "角色與裝備", "ko": "캐릭터 및 장비", "ja": "キャラクター・装備", "zh-CN": "角色与装备"},
  "g.progress": {"en": "Progress & unlocks", "zh-TW": "進度與解鎖", "ko": "진행 및 해금", "ja": "進行状況・解放", "zh-CN": "进度与解锁"},
  "g.modes": {"en": "Game modes", "zh-TW": "遊戲模式", "ko": "게임 모드", "ja": "ゲームモード", "zh-CN": "游戏模式"},
  "g.tools": {"en": "Tools", "zh-TW": "工具", "ko": "도구", "ja": "ツール", "zh-CN": "工具"},
  "c.item": {"en": "Items", "zh-TW": "道具", "ko": "아이템", "ja": "アイテム", "zh-CN": "道具"},
  "c.item.help": {"en": "Add, clear or reset items in your inventory.", "zh-TW": "新增、清除或重置背包中的道具。", "ko": "인벤토리의 아이템을 추가, 삭제 또는 초기화합니다.", "ja": "所持アイテムの追加・削除・初期化を行います。", "zh-CN": "添加、清除或重置背包中的道具。"},
  "c.bc": {"en": "Black Cards", "zh-TW": "黑卡", "ko": "블랙카드", "ja": "ブラックカード", "zh-CN": "黑卡"},
  "c.bc.help": {"en": "Grant Black Cards (default 30000).", "zh-TW": "發放黑卡(預設30000)。", "ko": "블랙카드를 지급합니다(기본 30000).", "ja": "ブラックカードを付与します(既定30000)。", "zh-CN": "发放黑卡(默认30000)。"},
  "c.character": {"en": "Characters", "zh-TW": "角色", "ko": "캐릭터", "ja": "キャラクター", "zh-CN": "角色"},
  "c.character.help": {"en": "Add characters, max them out, or set one stat of an owned character.", "zh-TW": "新增角色、將其全滿,或設定已擁有角色的單一數值。", "ko": "캐릭터를 추가하거나 최대로 올리거나 보유 캐릭터의 수치를 설정합니다.", "ja": "キャラクターの追加、最大化、所持キャラクターの個別設定を行います。", "zh-CN": "添加角色、将其升满,或设置已拥有角色的单项数值。"},
  "c.coating": {"en": "Coatings", "zh-TW": "塗裝", "ko": "코팅", "ja": "コーティング", "zh-CN": "涂装"},
  "c.coating.help": {"en": "Unlock coatings for one character, or all owned characters and weapons.", "zh-TW": "解鎖單一角色的塗裝,或所有已擁有角色與武器的塗裝。", "ko": "캐릭터 하나 또는 보유 중인 모든 캐릭터와 무기의 코팅을 해금합니다.", "ja": "1キャラクター、または所持中の全キャラクターと武器のコーティングを解放します。", "zh-CN": "解锁单个角色的涂装,或所有已拥有角色与武器的涂装。"},
  "c.equip": {"en": "Equipment", "zh-TW": "裝備", "ko": "장비", "ja": "装備", "zh-CN": "装备"},
  "c.equip.help": {"en": "Add, list and inspect equipment; set level, breakthrough, resonance and overrun; remove duplicates or reload.", "zh-TW": "新增、列出與檢視裝備;設定等級、突破、共鳴與超限;移除重複或重新載入。", "ko": "장비를 추가/목록/조회하고 레벨, 돌파, 공명, 초과를 설정하며 중복 삭제나 다시 불러오기를 합니다.", "ja": "装備の追加・一覧・確認、レベル・突破・共鳴・超限の設定、重複の削除や再読み込みを行います。", "zh-CN": "添加、列出与查看装备;设置等级、突破、共鸣与超限;移除重复或重新加载。"},
  "c.level": {"en": "Commandant level", "zh-TW": "指揮官等級", "ko": "지휘관 레벨", "ja": "指揮官レベル", "zh-CN": "指挥官等级"},
  "c.level.help": {"en": "Set your Commandant level, or the maximum.", "zh-TW": "設定指揮官等級,或設為最高等級。", "ko": "지휘관 레벨을 설정하거나 최대로 올립니다.", "ja": "指揮官レベルを設定、または最大にします。", "zh-CN": "设置指挥官等级,或设为最高等级。"},
  "c.stage": {"en": "Stages", "zh-TW": "關卡", "ko": "스테이지", "ja": "ステージ", "zh-CN": "关卡"},
  "c.stage.help": {"en": "Mark one stage, or every main stage, as cleared.", "zh-TW": "將單一關卡或所有主線關卡標記為已通關。", "ko": "스테이지 하나 또는 모든 메인 스테이지를 클리어 처리합니다.", "ja": "1ステージ、または全メインステージをクリア済みにします。", "zh-CN": "将单个关卡或所有主线关卡标记为已通关。"},
  "c.scene": {"en": "Home scenes", "zh-TW": "宿舍場景", "ko": "홈 장면", "ja": "ホームシーン", "zh-CN": "宿舍场景"},
  "c.scene.help": {"en": "Unlock every home scene background.", "zh-TW": "解鎖所有宿舍場景背景。", "ko": "모든 홈 장면 배경을 해금합니다.", "ja": "全てのホームシーン背景を解放します。", "zh-CN": "解锁所有宿舍场景背景。"},
  "c.warzone": {"en": "War Zone", "zh-TW": "戰區", "ko": "워존", "ja": "戦区", "zh-CN": "战区"},
  "c.warzone.help": {"en": "Tier, zone, fluctuation and score tuning for War Zone (also /wz).", "zh-TW": "戰區的階層、區域、波動與分數調整(亦可用 /wz)。", "ko": "워존의 티어, 구역, 변동, 점수 조정(/wz 도 가능).", "ja": "戦区のティア・ゾーン・変動・スコア調整(/wz も可)。", "zh-CN": "战区的阶层、区域、波动与分数调整(也可用 /wz)。"},
  "c.ppc": {"en": "PPC", "zh-TW": "PPC", "ko": "PPC", "ja": "PPC", "zh-CN": "PPC"},
  "c.ppc.help": {"en": "PPC tiers, attempts, codex scores and the intensive boss selection.", "zh-TW": "PPC的階層、挑戰次數、圖鑑分數與強襲首領選擇。", "ko": "PPC 티어, 도전 횟수, 도감 점수, 강습 보스 선택.", "ja": "PPCのティア・挑戦回数・図鑑スコア・強襲ボス選択。", "zh-CN": "PPC的阶层、挑战次数、图鉴分数与强袭首领选择。"},
  "c.save": {"en": "Save", "zh-TW": "儲存", "ko": "저장", "ja": "保存", "zh-CN": "保存"},
  "c.save.help": {"en": "Write your current session to the database.", "zh-TW": "將目前的連線狀態寫入資料庫。", "ko": "현재 세션을 데이터베이스에 저장합니다.", "ja": "現在のセッションをデータベースに書き込みます。", "zh-CN": "将当前会话写入数据库。"},
  "c.help": {"en": "Help", "zh-TW": "說明", "ko": "도움말", "ja": "ヘルプ", "zh-CN": "帮助"},
  "c.help.help": {"en": "Ask the server for its command list, or details of one command.", "zh-TW": "向伺服器查詢指令列表,或單一指令的詳細說明。", "ko": "서버에 명령어 목록 또는 특정 명령어의 설명을 요청합니다.", "ja": "サーバーにコマンド一覧、または個別コマンドの詳細を問い合わせます。", "zh-CN": "向服务器查询指令列表,或单个指令的详细说明。"},
  "a.item.add": {"en": "Add item", "zh-TW": "新增道具", "ko": "아이템 추가", "ja": "アイテムを追加", "zh-CN": "添加道具"},
  "a.item.addall": {"en": "Add every item", "zh-TW": "新增所有道具", "ko": "모든 아이템 추가", "ja": "全アイテムを追加", "zh-CN": "添加所有道具"},
  "a.item.clear": {"en": "Clear item", "zh-TW": "清除道具", "ko": "아이템 삭제", "ja": "アイテムを削除", "zh-CN": "清除道具"},
  "a.item.clearall": {"en": "Clear all items", "zh-TW": "清除所有道具", "ko": "모든 아이템 삭제", "ja": "全アイテムを削除", "zh-CN": "清除所有道具"},
  "a.item.reset": {"en": "Reset item", "zh-TW": "重置道具", "ko": "아이템 초기화", "ja": "アイテムを初期化", "zh-CN": "重置道具"},
  "a.item.resetall": {"en": "Reset all items", "zh-TW": "重置所有道具", "ko": "모든 아이템 초기화", "ja": "全アイテムを初期化", "zh-CN": "重置所有道具"},
  "a.bc.grant": {"en": "Grant Black Cards", "zh-TW": "發放黑卡", "ko": "블랙카드 지급", "ja": "ブラックカードを付与", "zh-CN": "发放黑卡"},
  "a.character.add": {"en": "Add character", "zh-TW": "新增角色", "ko": "캐릭터 추가", "ja": "キャラクターを追加", "zh-CN": "添加角色"},
  "a.character.addall": {"en": "Add all characters", "zh-TW": "新增所有角色", "ko": "모든 캐릭터 추가", "ja": "全キャラクターを追加", "zh-CN": "添加所有角色"},
  "a.coating.unlock": {"en": "Unlock coatings", "zh-TW": "解鎖塗裝", "ko": "코팅 해금", "ja": "コーティングを解放", "zh-CN": "解锁涂装"},
  "a.coating.unlockall": {"en": "Unlock all coatings", "zh-TW": "解鎖所有塗裝", "ko": "모든 코팅 해금", "ja": "全コーティングを解放", "zh-CN": "解锁所有涂装"},
  "a.equip.add": {"en": "Add equipment", "zh-TW": "新增裝備", "ko": "장비 추가", "ja": "装備を追加", "zh-CN": "添加装备"},
  "a.equip.addall": {"en": "Add all equipment", "zh-TW": "新增所有裝備", "ko": "모든 장비 추가", "ja": "全装備を追加", "zh-CN": "添加所有装备"},
  "a.equip.prune": {"en": "Remove duplicate weapons", "zh-TW": "移除重複武器", "ko": "중복 무기 삭제", "ja": "重複武器を削除", "zh-CN": "移除重复武器"},
  "a.equip.sync": {"en": "Reload equipment", "zh-TW": "重新載入裝備", "ko": "장비 다시 불러오기", "ja": "装備を再読み込み", "zh-CN": "重新加载装备"},
  "a.level.set": {"en": "Set level", "zh-TW": "設定等級", "ko": "레벨 설정", "ja": "レベルを設定", "zh-CN": "设置等级"},
  "a.level.max": {"en": "Max level", "zh-TW": "最高等級", "ko": "최대 레벨", "ja": "最大レベル", "zh-CN": "最高等级"},
  "a.stage.clear": {"en": "Clear stage", "zh-TW": "通關關卡", "ko": "스테이지 클리어", "ja": "ステージをクリア", "zh-CN": "通关关卡"},
  "a.stage.clearall": {"en": "Clear all main stages", "zh-TW": "通關所有主線關卡", "ko": "모든 메인 스테이지 클리어", "ja": "全メインステージをクリア", "zh-CN": "通关所有主线关卡"},
  "a.scene.unlockall": {"en": "Unlock all home scenes", "zh-TW": "解鎖所有宿舍場景", "ko": "모든 홈 장면 해금", "ja": "全ホームシーンを解放", "zh-CN": "解锁所有宿舍场景"},
  "a.warzone.tiers": {"en": "List tiers", "zh-TW": "列出階層", "ko": "티어 목록", "ja": "ティア一覧", "zh-CN": "列出阶层"},
  "a.warzone.tier": {"en": "Set tier", "zh-TW": "設定階層", "ko": "티어 설정", "ja": "ティアを設定", "zh-CN": "设置阶层"},
  "a.warzone.zones": {"en": "List zones", "zh-TW": "列出區域", "ko": "구역 목록", "ja": "ゾーン一覧", "zh-CN": "列出区域"},
  "a.warzone.set": {"en": "Set zone", "zh-TW": "設定區域", "ko": "구역 설정", "ja": "ゾーンを設定", "zh-CN": "设置区域"},
  "a.warzone.zone": {"en": "Set zone by AreaId", "zh-TW": "依AreaId設定區域", "ko": "AreaId로 구역 설정", "ja": "AreaIdでゾーンを設定", "zh-CN": "按AreaId设置区域"},
  "a.warzone.zoneauto": {"en": "Automatic zone", "zh-TW": "自動區域", "ko": "자동 구역", "ja": "ゾーンを自動に", "zh-CN": "自动区域"},
  "a.warzone.fluctuation": {"en": "Enemy level fluctuation", "zh-TW": "敵人等級波動", "ko": "적 레벨 변동", "ja": "敵レベル変動", "zh-CN": "敌人等级波动"},
  "a.warzone.dmg": {"en": "Set damage score (all players)", "zh-TW": "設定傷害分數(所有玩家)", "ko": "피해 점수 설정(모든 플레이어)", "ja": "ダメージスコアを設定(全プレイヤー)", "zh-CN": "设置伤害分数(所有玩家)"},
  "a.warzone.dmgreset": {"en": "Reset damage score (all players)", "zh-TW": "重置傷害分數(所有玩家)", "ko": "피해 점수 초기화(모든 플레이어)", "ja": "ダメージスコアを初期化(全プレイヤー)", "zh-CN": "重置伤害分数(所有玩家)"},
  "a.warzone.reset": {"en": "Reset War Zone", "zh-TW": "重置戰區", "ko": "워존 초기화", "ja": "戦区を初期化", "zh-CN": "重置战区"},
  "a.ppc.tiers": {"en": "List tiers", "zh-TW": "列出階層", "ko": "티어 목록", "ja": "ティア一覧", "zh-CN": "列出阶层"},
  "a.ppc.tier": {"en": "Set tier", "zh-TW": "設定階層", "ko": "티어 설정", "ja": "ティアを設定", "zh-CN": "设置阶层"},
  "a.ppc.reset": {"en": "Reset challenge attempts", "zh-TW": "重置挑戰次數", "ko": "도전 횟수 초기화", "ja": "挑戦回数を初期化", "zh-CN": "重置挑战次数"},
  "a.ppc.codexreset": {"en": "Reset codex scores", "zh-TW": "重置圖鑑分數", "ko": "도감 점수 초기화", "ja": "図鑑スコアを初期化", "zh-CN": "重置图鉴分数"},
  "a.ppc.attempts": {"en": "Set challenge attempts", "zh-TW": "設定挑戰次數", "ko": "도전 횟수 설정", "ja": "挑戦回数を設定", "zh-CN": "设置挑战次数"},
  "a.ppc.status": {"en": "Intensive: current selection", "zh-TW": "強襲:目前選擇", "ko": "강습: 현재 선택", "ja": "強襲:現在の選択", "zh-CN": "强袭:当前选择"},
  "a.ppc.bosses": {"en": "Intensive: list bosses", "zh-TW": "強襲:列出首領", "ko": "강습: 보스 목록", "ja": "強襲:ボス一覧", "zh-CN": "强袭:列出首领"},
  "a.ppc.boss": {"en": "Intensive: select boss", "zh-TW": "強襲:選擇首領", "ko": "강습: 보스 선택", "ja": "強襲:ボスを選択", "zh-CN": "强袭:选择首领"},
  "a.ppc.weaknesses": {"en": "Intensive: list weaknesses", "zh-TW": "強襲:列出弱點", "ko": "강습: 약점 목록", "ja": "強襲:弱点一覧", "zh-CN": "强袭:列出弱点"},
  "a.ppc.weakness": {"en": "Intensive: select weakness", "zh-TW": "強襲:選擇弱點", "ko": "강습: 약점 선택", "ja": "強襲:弱点を選択", "zh-CN": "强袭:选择弱点"},
  "a.ppc.intensivereset": {"en": "Intensive: reset to weekly default", "zh-TW": "強襲:重置為每週預設", "ko": "강습: 주간 기본값으로 초기화", "ja": "強襲:週間初期設定に戻す", "zh-CN": "强袭:重置为每周默认"},
  "a.save.save": {"en": "Save now", "zh-TW": "立即儲存", "ko": "지금 저장", "ja": "今すぐ保存", "zh-CN": "立即保存"},
  "a.help.help": {"en": "Show server help", "zh-TW": "顯示伺服器說明", "ko": "서버 도움말 표시", "ja": "サーバーのヘルプを表示", "zh-CN": "显示服务器帮助"},
  "f.itemId": {"en": "Item ID", "zh-TW": "道具ID", "ko": "아이템 ID", "ja": "アイテムID", "zh-CN": "道具ID"},
  "f.amount": {"en": "Amount", "zh-TW": "數量", "ko": "수량", "ja": "数量", "zh-CN": "数量"},
  "f.characterId": {"en": "Character ID", "zh-TW": "角色ID", "ko": "캐릭터 ID", "ja": "キャラクターID", "zh-CN": "角色ID"},
  "f.equipId": {"en": "Equipment ID", "zh-TW": "裝備ID", "ko": "장비 ID", "ja": "装備ID", "zh-CN": "装备ID"},
  "f.level": {"en": "Level", "zh-TW": "等級", "ko": "레벨", "ja": "レベル", "zh-CN": "等级"},
  "f.stageId": {"en": "Stage ID", "zh-TW": "關卡ID", "ko": "스테이지 ID", "ja": "ステージID", "zh-CN": "关卡ID"},
  "f.tier": {"en": "Tier", "zh-TW": "階層", "ko": "티어", "ja": "ティア", "zh-CN": "阶层"},
  "f.filter": {"en": "Filter", "zh-TW": "篩選", "ko": "필터", "ja": "絞り込み", "zh-CN": "筛选"},
  "f.zone": {"en": "Zone", "zh-TW": "區域", "ko": "구역", "ja": "ゾーン", "zh-CN": "区域"},
  "f.type": {"en": "Encounter type", "zh-TW": "遭遇類型", "ko": "조우 유형", "ja": "エンカウント種別", "zh-CN": "遭遇类型"},
  "f.areaId": {"en": "AreaId", "zh-TW": "AreaId", "ko": "AreaId", "ja": "AreaId", "zh-CN": "AreaId"},
  "f.fluctuation": {"en": "Fluctuation level (0 = off)", "zh-TW": "波動等級(0 = 關閉)", "ko": "변동 레벨(0 = 끔)", "ja": "変動レベル(0 = オフ)", "zh-CN": "波动等级(0 = 关闭)"},
  "f.percent": {"en": "Damage score percent (100 = default)", "zh-TW": "傷害分數百分比(100 = 預設)", "ko": "피해 점수 퍼센트(100 = 기본)", "ja": "ダメージスコア%(100 = 既定)", "zh-CN": "伤害分数百分比(100 = 默认)"},
  "f.levelType": {"en": "PPC LevelType ID", "zh-TW": "PPC LevelType ID", "ko": "PPC LevelType ID", "ja": "PPC LevelType ID", "zh-CN": "PPC LevelType ID"},
  "f.count": {"en": "Attempts", "zh-TW": "次數", "ko": "횟수", "ja": "回数", "zh-CN": "次数"},
  "f.boss": {"en": "Boss name or SectionId", "zh-TW": "首領名稱或SectionId", "ko": "보스 이름 또는 SectionId", "ja": "ボス名またはSectionId", "zh-CN": "首领名称或SectionId"},
  "f.group": {"en": "Weakness group", "zh-TW": "弱點組", "ko": "약점 그룹", "ja": "弱点グループ", "zh-CN": "弱点组"},
  "f.command": {"en": "Command name", "zh-TW": "指令名稱", "ko": "명령어 이름", "ja": "コマンド名", "zh-CN": "指令名称"},
  "w.items": {"en": "This permanently removes items from your account.", "zh-TW": "這會永久移除帳號中的道具。", "ko": "계정의 아이템이 영구적으로 삭제됩니다.", "ja": "アカウントのアイテムが完全に削除されます。", "zh-CN": "这会永久移除账号中的道具。"},
  "w.itemreset": {"en": "This overwrites item counts with the server defaults.", "zh-TW": "這會以伺服器預設值覆蓋道具數量。", "ko": "아이템 수량이 서버 기본값으로 덮어써집니다.", "ja": "アイテム数がサーバーの初期値で上書きされます。", "zh-CN": "这会以服务器默认值覆盖道具数量。"},
  "w.equip": {"en": "This deletes duplicate weapons from your account.", "zh-TW": "這會刪除帳號中重複的武器。", "ko": "계정의 중복 무기가 삭제됩니다.", "ja": "アカウントの重複武器が削除されます。", "zh-CN": "这会删除账号中重复的武器。"},
  "w.level": {"en": "Lowering your level logs you out. Raising it does not.", "zh-TW": "降低等級會使你登出;提高則不會。", "ko": "레벨을 낮추면 로그아웃됩니다. 올릴 때는 아닙니다.", "ja": "レベルを下げるとログアウトされます。上げる場合は問題ありません。", "zh-CN": "降低等级会使你登出;提高则不会。"},
  "w.stage": {"en": "This replaces all your main-stage records with a cleared state.", "zh-TW": "這會把所有主線關卡紀錄替換為已通關狀態。", "ko": "모든 메인 스테이지 기록이 클리어 상태로 교체됩니다.", "ja": "全メインステージの記録がクリア済みの状態に置き換わります。", "zh-CN": "这会把所有主线关卡记录替换为已通关状态。"},
  "w.dmg": {"en": "This changes War Zone scoring for EVERY player on this server.", "zh-TW": "這會改變此伺服器上所有玩家的戰區計分。", "ko": "이 서버의 모든 플레이어의 워존 점수에 영향을 줍니다.", "ja": "このサーバーの全プレイヤーの戦区スコアが変わります。", "zh-CN": "这会改变此服务器上所有玩家的战区计分。"},
  "w.reset": {"en": "This resets your progress for this mode.", "zh-TW": "這會重置你在此模式的進度。", "ko": "이 모드의 진행 상황이 초기화됩니다.", "ja": "このモードの進行状況が初期化されます。", "zh-CN": "这会重置你在此模式的进度。"},
  "u.title": {"en": "AscNet Menu", "zh-TW": "AscNet 選單", "ko": "AscNet 메뉴", "ja": "AscNet メニュー", "zh-CN": "AscNet 菜单"},
  "u.runconfirm": {"en": "Run...", "zh-TW": "執行...", "ko": "실행...", "ja": "実行...", "zh-CN": "执行..."},
  "f.amount.hint": {"en": "Number or max", "zh-TW": "數字或 max", "ko": "숫자 또는 max", "ja": "数値または max", "zh-CN": "数字或 max"},
  "f.tier.hint": {"en": "ID or name", "zh-TW": "ID或名稱", "ko": "ID 또는 이름", "ja": "IDまたは名前", "zh-CN": "ID或名称"},
  "f.filter.hint": {"en": "Element or zone name", "zh-TW": "屬性或區域名稱", "ko": "속성 또는 구역 이름", "ja": "属性またはゾーン名", "zh-CN": "属性或区域名称"},
  "f.zone.hint": {"en": "Element, name or AreaId", "zh-TW": "屬性、名稱或AreaId", "ko": "속성, 이름 또는 AreaId", "ja": "属性・名前・AreaId", "zh-CN": "属性、名称或AreaId"},
  "u.run": {"en": "Run", "zh-TW": "執行", "ko": "실행", "ja": "実行", "zh-CN": "执行"},
  "u.confirm": {"en": "Review and confirm", "zh-TW": "檢查並確認", "ko": "확인 후 실행", "ja": "確認して実行", "zh-CN": "检查并确认"},
  "u.noreply": {"en": "(no reply yet)", "zh-TW": "(尚無回覆)", "ko": "(아직 응답 없음)", "ja": "(まだ返信なし)", "zh-CN": "(尚无回复)"},
  "u.sending": {"en": "Sent: /%s", "zh-TW": "已送出:/%s", "ko": "전송됨: /%s", "ja": "送信しました: /%s", "zh-CN": "已发送:/%s"},
  "u.timeout": {"en": "No reply from the server. Check that you are logged in and the server is running.", "zh-TW": "伺服器沒有回覆。請確認已登入且伺服器正在運行。", "ko": "서버 응답이 없습니다. 로그인 상태와 서버 실행 여부를 확인하세요.", "ja": "サーバーから返信がありません。ログイン状態とサーバーの稼働を確認してください。", "zh-CN": "服务器没有回复。请确认已登录且服务器正在运行。"},
  "u.needvalue": {"en": "Fill in: %s", "zh-TW": "請填寫:%s", "ko": "입력하세요: %s", "ja": "入力してください: %s", "zh-CN": "请填写:%s"},
  "u.number": {"en": "%s must be a whole number", "zh-TW": "%s 必須為整數", "ko": "%s 은(는) 정수여야 합니다", "ja": "%s は整数で入力してください", "zh-CN": "%s 必须为整数"},
  "u.allowed": {"en": "%s: a whole number or %s", "zh-TW": "%s:整數或 %s", "ko": "%s: 정수 또는 %s", "ja": "%s: 整数または %s", "zh-CN": "%s:整数或 %s"},
  "u.gap": {"en": "Fill in %s too, or clear the later fields", "zh-TW": "請一併填寫 %s,或清空後面的欄位", "ko": "%s 도 입력하거나 뒤의 항목을 비우세요", "ja": "%s も入力するか、後ろの項目を空にしてください", "zh-CN": "请一并填写 %s,或清空后面的字段"},
  "u.nospace": {"en": "%s cannot contain spaces", "zh-TW": "%s 不可包含空白", "ko": "%s 에는 공백을 쓸 수 없습니다", "ja": "%s に空白は使えません", "zh-CN": "%s 不可包含空格"},
  "u.optional": {"en": "(optional)", "zh-TW": "(選填)", "ko": "(선택)", "ja": "(任意)", "zh-CN": "(选填)"},
  "u.any": {"en": "(none)", "zh-TW": "(無)", "ko": "(없음)", "ja": "(なし)", "zh-CN": "(无)"},
  "u.enter": {"en": "Enter value", "zh-TW": "輸入數值", "ko": "값 입력", "ja": "値を入力", "zh-CN": "输入数值"},
  "u.line": {"en": "Command", "zh-TW": "指令", "ko": "명령어", "ja": "コマンド", "zh-CN": "指令"},
  "u.notready": {"en": "Not logged in yet: the chat service is not available.", "zh-TW": "尚未登入:聊天服務不可用。", "ko": "아직 로그인하지 않았습니다: 채팅을 사용할 수 없습니다.", "ja": "未ログインのためチャットを利用できません。", "zh-CN": "尚未登录:聊天服务不可用。"},
  "u.search": {"en": "Type an id or part of a name", "zh-TW": "輸入編號或部分名稱", "ko": "ID 또는 이름 일부 입력", "ja": "IDまたは名前の一部を入力", "zh-CN": "输入编号或部分名称"},
  "u.nomatch": {"en": "No match", "zh-TW": "查無符合", "ko": "일치 없음", "ja": "一致なし", "zh-CN": "无匹配"},
  "u.lv": {"en": "Lv.%s", "zh-TW": "Lv.%s", "ko": "Lv.%s", "ja": "Lv.%s", "zh-CN": "Lv.%s"},
  "u.wornby": {"en": "worn by %s", "zh-TW": "%s 裝備中", "ko": "%s 착용 중", "ja": "%s が装備中", "zh-CN": "%s 装备中"},
  "u.unworn": {"en": "not worn", "zh-TW": "未裝備", "ko": "미착용", "ja": "未装備", "zh-CN": "未装备"},
  "u.carriedby": {"en": "carried by %s", "zh-TW": "%s 攜帶中", "ko": "%s 휴대 중", "ja": "%s が所持", "zh-CN": "%s 携带中"},
  "u.uncarried": {"en": "not carried", "zh-TW": "未攜帶", "ko": "미휴대", "ja": "未所持", "zh-CN": "未携带"},
  "l.item": {"en": "Item", "zh-TW": "道具", "ko": "아이템", "ja": "アイテム", "zh-CN": "道具"},
  "l.weapon": {"en": "Weapon", "zh-TW": "武器", "ko": "무기", "ja": "武器", "zh-CN": "武器"},
  "l.memory": {"en": "Memory", "zh-TW": "意識", "ko": "의식", "ja": "意識", "zh-CN": "意识"},
  "l.suit": {"en": "Memory set", "zh-TW": "意識套裝", "ko": "의식 세트", "ja": "意識セット", "zh-CN": "意识套装"},
  "l.character": {"en": "Character", "zh-TW": "角色", "ko": "캐릭터", "ja": "キャラクター", "zh-CN": "角色"},
  "l.cub": {"en": "Cub", "zh-TW": "方塊獸", "ko": "큐브", "ja": "キューブ", "zh-CN": "方块兽"},
  "l.stage": {"en": "Stage", "zh-TW": "關卡", "ko": "스테이지", "ja": "ステージ", "zh-CN": "关卡"},
  "l.medal": {"en": "Medal", "zh-TW": "勳章", "ko": "훈장", "ja": "メダル", "zh-CN": "勋章"},
  "l.nameplate": {"en": "Nameplate", "zh-TW": "銘牌", "ko": "명패", "ja": "ネームプレート", "zh-CN": "铭牌"},
  "l.emoji": {"en": "Emoji", "zh-TW": "表情", "ko": "이모티콘", "ja": "スタンプ", "zh-CN": "表情"},
  "l.title": {"en": "Title", "zh-TW": "稱號", "ko": "칭호", "ja": "称号", "zh-CN": "称号"},
  "l.chatboard": {"en": "Chat board", "zh-TW": "聊天框", "ko": "채팅 보드", "ja": "チャットボード", "zh-CN": "聊天框"},
  "l.portrait": {"en": "Portrait", "zh-TW": "頭像", "ko": "초상화", "ja": "ポートレート", "zh-CN": "头像"},
  "l.furniture": {"en": "Furniture", "zh-TW": "家具", "ko": "가구", "ja": "家具", "zh-CN": "家具"},
  "g.cosmetics": {"en": "Cosmetics", "zh-TW": "外觀", "ko": "꾸미기", "ja": "コスメティック", "zh-CN": "外观"},
  "c.unlock": {"en": "Cosmetics", "zh-TW": "外觀", "ko": "꾸미기", "ja": "コスメティック", "zh-CN": "外观"},
  "c.unlock.help": {"en": "Unlock medals, nameplates, emoji, titles, chat boards and portraits, or add dorm furniture.", "zh-TW": "解鎖勳章、銘牌、表情、稱號、聊天框與頭像,或新增宿舍家具。", "ko": "훈장, 명패, 이모티콘, 칭호, 채팅 보드, 초상화를 해금하거나 기숙사 가구를 추가합니다.", "ja": "メダル、ネームプレート、スタンプ、称号、チャットボード、ポートレートを解放、または寮の家具を追加します。", "zh-CN": "解锁勋章、铭牌、表情、称号、聊天框与头像,或添加宿舍家具。"},
  "c.cub": {"en": "Cubs", "zh-TW": "方塊獸", "ko": "큐브", "ja": "キューブ", "zh-CN": "方块兽"},
  "c.cub.help": {"en": "Add cubs and set their level, quality and skills.", "zh-TW": "新增方塊獸並設定等級、品質與技能。", "ko": "큐브를 추가하고 레벨, 품질, 스킬을 설정합니다.", "ja": "キューブを追加し、レベル・品質・スキルを設定します。", "zh-CN": "添加方块兽并设置等级、品质与技能。"},
  "w.charmax": {"en": "This rewrites the character: level, quality, grade, trust, awakening and every skill go to their maximum.", "zh-TW": "這會改寫角色:等級、品質、階級、信賴、覺醒與所有技能都會升到最高。", "ko": "캐릭터가 덮어써집니다: 레벨, 품질, 등급, 신뢰, 각성, 모든 스킬이 최대가 됩니다.", "ja": "キャラクターが書き換わります。レベル・品質・グレード・信頼・覚醒・全スキルが最大になります。", "zh-CN": "这会改写角色:等级、品质、阶级、信赖、觉醒与所有技能都会升到最高。"},
  "w.cubmax": {"en": "This sets the cub to its maximum level, quality and skills.", "zh-TW": "這會把方塊獸設為最高等級、品質與技能。", "ko": "큐브의 레벨, 품질, 스킬이 최대로 설정됩니다.", "ja": "キューブのレベル・品質・スキルが最大になります。", "zh-CN": "这会把方块兽设为最高等级、品质与技能。"},
  "a.character.max": {"en": "Max out", "zh-TW": "全滿", "ko": "최대로 올리기", "ja": "最大化", "zh-CN": "升满"},
  "a.character.maxgear": {"en": "Max out and wear recommended gear", "zh-TW": "全滿並穿上推薦裝備", "ko": "최대로 올리고 추천 장비 착용", "ja": "最大化して推奨装備を装着", "zh-CN": "升满并穿戴推荐装备"},
  "a.character.set": {"en": "Set one stat", "zh-TW": "設定單一數值", "ko": "수치 설정", "ja": "項目を設定", "zh-CN": "设置单项数值"},
  "a.character.setskill": {"en": "Set skill level", "zh-TW": "設定技能等級", "ko": "스킬 레벨 설정", "ja": "スキルレベルを設定", "zh-CN": "设置技能等级"},
  "a.equip.list": {"en": "List equipment", "zh-TW": "列出裝備", "ko": "장비 목록", "ja": "装備一覧", "zh-CN": "列出装备"},
  "a.equip.inspect": {"en": "Inspect equipment", "zh-TW": "檢視裝備", "ko": "장비 조회", "ja": "装備を確認", "zh-CN": "查看装备"},
  "a.equip.level": {"en": "Set level", "zh-TW": "設定等級", "ko": "레벨 설정", "ja": "レベルを設定", "zh-CN": "设置等级"},
  "a.equip.breakthrough": {"en": "Set breakthrough", "zh-TW": "設定突破", "ko": "돌파 설정", "ja": "突破を設定", "zh-CN": "设置突破"},
  "a.equip.resclear": {"en": "Clear resonance", "zh-TW": "清除共鳴", "ko": "공명 해제", "ja": "共鳴を解除", "zh-CN": "清除共鸣"},
  "a.equip.resattrib": {"en": "Resonance: attribute", "zh-TW": "共鳴:屬性", "ko": "공명: 속성", "ja": "共鳴:属性", "zh-CN": "共鸣:属性"},
  "a.equip.resskill": {"en": "Resonance: skill", "zh-TW": "共鳴:技能", "ko": "공명: 스킬", "ja": "共鳴:スキル", "zh-CN": "共鸣:技能"},
  "a.equip.resweapon": {"en": "Resonance: weapon skill", "zh-TW": "共鳴:武器技能", "ko": "공명: 무기 스킬", "ja": "共鳴:武器スキル", "zh-CN": "共鸣:武器技能"},
  "a.equip.overrun": {"en": "Set overrun level", "zh-TW": "設定超限等級", "ko": "초과 레벨 설정", "ja": "超限レベルを設定", "zh-CN": "设置超限等级"},
  "a.equip.overrunsuit": {"en": "Set overrun suit", "zh-TW": "設定超限套裝", "ko": "초과 세트 설정", "ja": "超限セットを設定", "zh-CN": "设置超限套装"},
  "a.cub.list": {"en": "List cubs", "zh-TW": "列出方塊獸", "ko": "큐브 목록", "ja": "キューブ一覧", "zh-CN": "列出方块兽"},
  "a.cub.add": {"en": "Add cub", "zh-TW": "新增方塊獸", "ko": "큐브 추가", "ja": "キューブを追加", "zh-CN": "添加方块兽"},
  "a.cub.level": {"en": "Set level", "zh-TW": "設定等級", "ko": "레벨 설정", "ja": "レベルを設定", "zh-CN": "设置等级"},
  "a.cub.quality": {"en": "Set quality", "zh-TW": "設定品質", "ko": "품질 설정", "ja": "品質を設定", "zh-CN": "设置品质"},
  "a.cub.skill": {"en": "Set skill level", "zh-TW": "設定技能等級", "ko": "스킬 레벨 설정", "ja": "スキルレベルを設定", "zh-CN": "设置技能等级"},
  "a.cub.max": {"en": "Max out", "zh-TW": "全滿", "ko": "최대로 올리기", "ja": "最大化", "zh-CN": "升满"},
  "a.unlock.unlock": {"en": "Unlock", "zh-TW": "解鎖", "ko": "해금", "ja": "解放", "zh-CN": "解锁"},
  "a.unlock.furniture": {"en": "Add furniture", "zh-TW": "新增家具", "ko": "가구 추가", "ja": "家具を追加", "zh-CN": "添加家具"},
  "f.characterId.hint": {"en": "ID or all", "zh-TW": "ID或all", "ko": "ID 또는 all", "ja": "IDまたは all", "zh-CN": "ID或all"},
  "f.stat": {"en": "Stat", "zh-TW": "數值", "ko": "수치", "ja": "項目", "zh-CN": "数值"},
  "f.value": {"en": "Value", "zh-TW": "數值", "ko": "값", "ja": "値", "zh-CN": "数值"},
  "f.skillId": {"en": "Skill ID", "zh-TW": "技能ID", "ko": "스킬 ID", "ja": "スキルID", "zh-CN": "技能ID"},
  "f.recordId": {"en": "Equipment record", "zh-TW": "裝備紀錄", "ko": "장비 기록", "ja": "装備レコード", "zh-CN": "装备记录"},
  "f.recordId.hint": {"en": "From List", "zh-TW": "來自列表", "ko": "목록 참고", "ja": "一覧から", "zh-CN": "来自列表"},
  "f.equipTarget": {"en": "Target", "zh-TW": "目標", "ko": "대상", "ja": "対象", "zh-CN": "目标"},
  "f.equipTarget.hint": {"en": "Record, all or c<ID>", "zh-TW": "紀錄、all或c<ID>", "ko": "기록, all 또는 c<ID>", "ja": "レコード、all、c<ID>", "zh-CN": "记录、all或c<ID>"},
  "f.show": {"en": "Show", "zh-TW": "顯示", "ko": "표시", "ja": "表示", "zh-CN": "显示"},
  "f.levelMax": {"en": "Level", "zh-TW": "等級", "ko": "레벨", "ja": "レベル", "zh-CN": "等级"},
  "f.levelMax.hint": {"en": "Number or max", "zh-TW": "數字或 max", "ko": "숫자 또는 max", "ja": "数値または max", "zh-CN": "数字或 max"},
  "f.step": {"en": "Step", "zh-TW": "階段", "ko": "단계", "ja": "段階", "zh-CN": "阶段"},
  "f.slot": {"en": "Resonance slot", "zh-TW": "共鳴欄位", "ko": "공명 슬롯", "ja": "共鳴スロット", "zh-CN": "共鸣栏位"},
  "f.poolId": {"en": "Attribute pool ID", "zh-TW": "屬性池ID", "ko": "속성 풀 ID", "ja": "属性プールID", "zh-CN": "属性池ID"},
  "f.boundChar.hint": {"en": "Defaults to the wearer", "zh-TW": "預設為裝備者", "ko": "기본값: 착용자", "ja": "既定は装着者", "zh-CN": "默认为装备者"},
  "f.suitId": {"en": "Suit ID", "zh-TW": "套裝ID", "ko": "세트 ID", "ja": "セットID", "zh-CN": "套装ID"},
  "f.cubRecord": {"en": "Cub record", "zh-TW": "方塊獸紀錄", "ko": "큐브 기록", "ja": "キューブ記録", "zh-CN": "方块兽记录"},
  "f.cubRecord.hint": {"en": "From List, or all", "zh-TW": "來自列表,或all", "ko": "목록 참고 또는 all", "ja": "一覧から、または all", "zh-CN": "来自列表,或all"},
  "f.cubTemplate": {"en": "Template ID", "zh-TW": "範本ID", "ko": "템플릿 ID", "ja": "テンプレートID", "zh-CN": "模板ID"},
  "f.cubTemplate.hint": {"en": "ID or all", "zh-TW": "ID或all", "ko": "ID 또는 all", "ja": "IDまたは all", "zh-CN": "ID或all"},
  "f.cubList": {"en": "Show", "zh-TW": "顯示", "ko": "표시", "ja": "表示", "zh-CN": "显示"},
  "f.overclock": {"en": "Overclock", "zh-TW": "超頻", "ko": "오버클럭", "ja": "オーバークロック", "zh-CN": "超频"},
  "f.quality": {"en": "Quality", "zh-TW": "品質", "ko": "품질", "ja": "品質", "zh-CN": "品质"},
  "f.kind": {"en": "Kind", "zh-TW": "類型", "ko": "종류", "ja": "種類", "zh-CN": "类型"},
  "f.unlockId": {"en": "ID", "zh-TW": "ID", "ko": "ID", "ja": "ID", "zh-CN": "ID"},
  "f.unlockId.hint": {"en": "ID or all", "zh-TW": "ID或all", "ko": "ID 또는 all", "ja": "IDまたは all", "zh-CN": "ID或all"},
  "f.furnitureId": {"en": "Furniture ID", "zh-TW": "家具ID", "ko": "가구 ID", "ja": "家具ID", "zh-CN": "家具ID"},
  "f.furnCount": {"en": "Count", "zh-TW": "數量", "ko": "수량", "ja": "数量", "zh-CN": "数量"},
  "f.furnCount.hint": {"en": "Default 1", "zh-TW": "預設1", "ko": "기본값 1", "ja": "既定は1", "zh-CN": "默认1"}
 }
}
]==])
