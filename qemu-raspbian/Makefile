.DEFAULT: run-raspi4
.DELETE_ON_ERROR:

TOP_BUILDDIR := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
MNT_DIR := $(TOP_BUILDDIR)/mnt

include .env.mk
export $(shell sed -e 's/^#.*//' -e 's/[:?]\?=.*//' -e '/^$$/d' .env.mk)

IMG_QCOW2 := $(patsubst %.img,%.qcow2,$(IMG))

.PHONY: print-img-qcow2
print-img-qcow2:
	@printf '%s\n' '$(TOP_BUILDDIR)/$(IMG_QCOW2)'

QEMU_BOOT_FILES := kernel8.img bcm2710-rpi-3-b.dtb bcm2711-rpi-4-b.dtb

export MNT_DIR

$(IMG):
	./get-rpi-img.sh

$(IMG_QCOW2): $(IMG)
	./prep-qcow2.sh

$(QEMU_BOOT_FILES) &: | $(IMG_QCOW2)
	./extract-boot-files.sh
	touch $(QEMU_BOOT_FILES)


.SECONDARY: $(subst .dtb,.dts,$(filter %.dtb,$(QEMU_BOOT_FILES))) $(subst .dtb,.dts.patched,$(filter %.dtb,$(QEMU_BOOT_FILES)))
%.dtb: $(QEMU_BOOT_FILES)
%.dts: %.dtb
	dtc -I dtb -O dts -o $@ $*.dtb
	touch $@

%.dts.patched: %.dts.patch %.dts
	cp $*.dts $*.dts.bak
	cd "$(TOP_BUILDDIR)" && patch -p1 < '$<'
	mv $*.dts $*.dts.patched
	mv $*.dts.bak $*.dts
	touch $*.dts.patched

%.mod.dtb: %.dts.patched
	dtc -I dts -O dtb -o $@ $*.dts.patched
	chmod +x $@

export IMG_QCOW2
.PHONY: run-raspi4 run-raspi3 prep-image prepare-multi-base prep-instances \
	reset-instances update-instance-passwords run-all stop-all force-stop-all status \
	run-instance ssh-instance stop-instance force-stop-instance \
	sync-replay-config sync-replay-config-all reset-replay-state
prep-image: | $(IMG_QCOW2)

# Internal target used by manage-instances.sh after its safety checks pass.
prepare-multi-base: | $(QEMU_BOOT_FILES) bcm2711-rpi-4-b.mod.dtb

prep-instances:
	$(TOP_BUILDDIR)/manage-instances.sh prep

reset-instances:
	CONFIRM_RESET="$(CONFIRM_RESET)" $(TOP_BUILDDIR)/manage-instances.sh reset

update-instance-passwords:
	$(TOP_BUILDDIR)/manage-instances.sh update-passwords

run-all:
	$(TOP_BUILDDIR)/manage-instances.sh run-all

stop-all:
	$(TOP_BUILDDIR)/manage-instances.sh stop-all

force-stop-all:
	$(TOP_BUILDDIR)/manage-instances.sh force-stop-all

status:
	$(TOP_BUILDDIR)/manage-instances.sh status

run-instance:
	@test -n "$(INSTANCE)" || { echo "Usage: make run-instance INSTANCE=pi4-1" >&2; exit 2; }
	$(TOP_BUILDDIR)/manage-instances.sh run "$(INSTANCE)"

ssh-instance:
	@test -n "$(INSTANCE)" || { echo "Usage: make ssh-instance INSTANCE=pi4-1" >&2; exit 2; }
	$(TOP_BUILDDIR)/manage-instances.sh connect "$(INSTANCE)"

ssh-pi4-%:
	$(TOP_BUILDDIR)/manage-instances.sh connect "pi4-$*"

stop-instance:
	@test -n "$(INSTANCE)" || { echo "Usage: make stop-instance INSTANCE=pi4-1" >&2; exit 2; }
	$(TOP_BUILDDIR)/manage-instances.sh stop "$(INSTANCE)"

force-stop-instance:
	@test -n "$(INSTANCE)" || { echo "Usage: make force-stop-instance INSTANCE=pi4-1" >&2; exit 2; }
	$(TOP_BUILDDIR)/manage-instances.sh force-stop "$(INSTANCE)"

sync-replay-config:
	@test -n "$(INSTANCE)" || { echo "Usage: make sync-replay-config INSTANCE=pi4-1" >&2; exit 2; }
	$(TOP_BUILDDIR)/manage-instances.sh sync-replay-config "$(INSTANCE)"

sync-replay-config-all:
	$(TOP_BUILDDIR)/manage-instances.sh sync-replay-config-all

reset-replay-state:
	@test -n "$(INSTANCE)" || { echo "Usage: make reset-replay-state INSTANCE=pi4-1" >&2; exit 2; }
	$(TOP_BUILDDIR)/manage-instances.sh reset-replay-state "$(INSTANCE)"

run-raspi4: | $(QEMU_BOOT_FILES) $(subst .dtb,.mod.dtb,$(filter-out bcm2710-rpi-3-b.dtb,$(QEMU_BOOT_FILES)))
	$(TOP_BUILDDIR)/run-raspi4.sh

run-raspi3: | $(QEMU_BOOT_FILES) $(filter-out bcm2711-rpi-4-b.dtb,$(QEMU_BOOT_FILES))
	$(TOP_BUILDDIR)/run-raspi3.sh

clean::
	rm -f $(QEMU_BOOT_FILES)


clean-all:: clean
	rm -f "$(IMG)"
	rm -f "$(IMG_QCOW2)"
	rm -f *.dts *.dtb *.dts.patched
