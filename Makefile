# Thin, stable wrapper over the dotnet CLI (spec 21.3). Same commands locally and in CI.
SLN        := Flowd.slnx
QUIET      := -v:q -clp:ErrorsOnly
ALL_TESTS  := $(wildcard tests/*.Tests)
UNIT_TESTS := $(filter-out tests/Flowd.Conformance.Tests tests/Flowd.Integration.Tests tests/Flowd.Architecture.Tests,$(ALL_TESTS))
COVER_DIR  := artifacts/coverage
N          ?= 20

.PHONY: lint test-unit test-conformance test-integration test-repeat cover ci

lint:
	dotnet format $(SLN) --verify-no-changes -v:q
	dotnet build $(SLN) -warnaserror $(QUIET)
	dotnet test --project tests/Flowd.Architecture.Tests --no-build -v:q

test-unit:
	dotnet build $(SLN) $(QUIET)
	@for p in $(UNIT_TESTS); do dotnet test --project $$p --no-build -v:q || exit 1; done

test-conformance:
	dotnet build tests/Flowd.Conformance.Tests $(QUIET)
	dotnet test --project tests/Flowd.Conformance.Tests --no-build -v:q

test-integration:
	dotnet build tests/Flowd.Integration.Tests $(QUIET)
	dotnet test --project tests/Flowd.Integration.Tests --no-build -v:q

test-repeat:
	@test -n "$(PROJECT)" || (echo "usage: make test-repeat PROJECT=tests/Flowd.X.Tests N=20" && exit 1)
	dotnet build $(PROJECT) $(QUIET)
	@i=1; while [ $$i -le $(N) ]; do dotnet test --project $(PROJECT) --no-build -v:q || exit 1; i=$$((i+1)); done

cover:
	rm -rf $(COVER_DIR)
	dotnet build $(SLN) $(QUIET)
	@for p in $(ALL_TESTS); do dotnet test --project $$p --no-build -v:q --coverage --coverage-output-format cobertura --results-directory $(COVER_DIR)/raw || exit 1; done
	dotnet tool restore -v q
	dotnet reportgenerator "-reports:$(COVER_DIR)/raw/**/*.cobertura.xml" "-targetdir:$(COVER_DIR)/report" -reporttypes:Cobertura "-assemblyfilters:+Flowd.*;-*.Tests" -verbosity:Warning
	sh scripts/check-coverage.sh $(COVER_DIR)/report/Cobertura.xml

ci: lint test-unit test-conformance test-integration cover
